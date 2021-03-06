﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Foundatio.Caching;
using Foundatio.Logging;
using Foundatio.Parsers.ElasticQueries.Extensions;
using Foundatio.Repositories.Elasticsearch.Configuration;
using Foundatio.Repositories.Elasticsearch.Extensions;
using Foundatio.Repositories.Elasticsearch.Queries.Builders;
using Foundatio.Repositories.Extensions;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Options;
using Foundatio.Utility;
using Nest;

namespace Foundatio.Repositories.Elasticsearch {
    public abstract class ElasticReadOnlyRepositoryBase<T> : IElasticReadOnlyRepository<T> where T : class, new() {
        protected static readonly bool HasIdentity = typeof(IIdentity).IsAssignableFrom(typeof(T));
        protected static readonly bool HasDates = typeof(IHaveDates).IsAssignableFrom(typeof(T));
        protected static readonly bool HasCreatedDate = typeof(IHaveCreatedDate).IsAssignableFrom(typeof(T));
        protected static readonly bool SupportsSoftDeletes = typeof(ISupportSoftDeletes).IsAssignableFrom(typeof(T));
        protected static readonly bool HasVersion = typeof(IVersioned).IsAssignableFrom(typeof(T));
        protected static readonly string EntityTypeName = typeof(T).Name;
        protected static readonly IReadOnlyCollection<T> EmptyList = new List<T>(0).AsReadOnly();
        protected readonly string _idField = null;

        protected readonly ILogger _logger;
        protected readonly IElasticClient _client;

        private ScopedCacheClient _scopedCacheClient;

        protected ElasticReadOnlyRepositoryBase(IIndexType<T> indexType) {
            ElasticType = indexType;
            if (HasIdentity)
                _idField = indexType.GetFieldName((T doc) => ((IIdentity)doc).Id) ?? "id";
            _client = indexType.Configuration.Client;
            SetCache(indexType.Configuration.Cache);
            _logger = indexType.Configuration.LoggerFactory.CreateLogger(GetType());
        }

        public Task<FindResults<T>> FindAsync(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) {
            return FindAsAsync<T>(query.Configure(), options.Configure());
        }

        public Task<FindResults<T>> FindAsync(IRepositoryQuery query, ICommandOptions options = null) {
            return FindAsAsync<T>(query, options);
        }

        protected ICollection<Field> DefaultExcludes { get; } = new List<Field>();

        public Task<FindResults<TResult>> FindAsAsync<TResult>(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) where TResult : class, new() {
            return FindAsAsync<TResult>(query.Configure(), options.Configure());
        }

        public async Task<FindResults<TResult>> FindAsAsync<TResult>(IRepositoryQuery query, ICommandOptions options = null) where TResult : class, new() {
            if (query == null)
                query = new RepositoryQuery();

            bool useSnapshotPaging = options.ShouldUseSnapshotPaging();
            // don't use caching with snapshot paging.
            bool allowCaching = IsCacheEnabled && useSnapshotPaging == false;

            options = ConfigureOptions(options);
            await OnBeforeQueryAsync(query, options, typeof(TResult)).AnyContext();

            Func<FindResults<TResult>, Task<FindResults<TResult>>> getNextPageFunc = async r => {
                var previousResults = r;
                if (previousResults == null)
                    throw new ArgumentException(nameof(r));

                if (!String.IsNullOrEmpty(previousResults.GetScrollId())) {
                    var scrollResponse = await _client.ScrollAsync<TResult>(options.GetSnapshotLifetime(), previousResults.GetScrollId()).AnyContext();
                    _logger.Trace(() => scrollResponse.GetRequest());

                    var results = scrollResponse.ToFindResults();
                    results.Page = previousResults.Page + 1;
                    results.HasMore = scrollResponse.Hits.Count() >= options.GetLimit();
                    return results;
                }

                if (options == null)
                    return new FindResults<TResult>();

                options?.PageNumber(!options.HasPageNumber() ? 2 : options.GetPage() + 1);

                return await FindAsAsync<TResult>(query, options).AnyContext();
            };

            string cacheSuffix = options?.HasPageLimit() == true ? String.Concat(options.GetPage().ToString(), ":", options.GetLimit().ToString()) : null;

            FindResults<TResult> result;
            if (allowCaching) {
                result = await GetCachedQueryResultAsync<FindResults<TResult>>(options, cacheSuffix: cacheSuffix).AnyContext();
                if (result != null) {
                    ((IGetNextPage<TResult>)result).GetNextPageFunc = async r => await getNextPageFunc(r).AnyContext();
                    return result;
                }
            }

            ISearchResponse<TResult> response = null;

            if (useSnapshotPaging == false || !options.HasSnapshotScrollId()) {
                var searchDescriptor = await CreateSearchDescriptorAsync(query, options).AnyContext();
                if (useSnapshotPaging)
                    searchDescriptor.Scroll(options.GetSnapshotLifetime());

                response = await _client.SearchAsync<TResult>(searchDescriptor).AnyContext();
            } else {
                response = await _client.ScrollAsync<TResult>(options.GetSnapshotLifetime(), options.GetSnapshotScrollId()).AnyContext();
            }

            _logger.Trace(() => response.GetRequest());
            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return new FindResults<TResult>();

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            if (useSnapshotPaging) {
                result = response.ToFindResults();
                // TODO: Is there a better way to figure out if you are done scrolling?
                result.HasMore = response.Hits.Count() >= options.GetLimit();
                ((IGetNextPage<TResult>)result).GetNextPageFunc = getNextPageFunc;
            } else if (options.HasPageLimit() == true) {
                result = response.ToFindResults(options.GetLimit());
                result.HasMore = response.Hits.Count() > options.GetLimit();
                ((IGetNextPage<TResult>)result).GetNextPageFunc = getNextPageFunc;
            } else {
                result = response.ToFindResults();
            }

            result.Page = options.GetPage();

            if (!allowCaching)
                return result;

            var nextPageFunc = ((IGetNextPage<TResult>)result).GetNextPageFunc;
            ((IGetNextPage<TResult>)result).GetNextPageFunc = null;
            await SetCachedQueryResultAsync(options, result, cacheSuffix: cacheSuffix).AnyContext();
            ((IGetNextPage<TResult>)result).GetNextPageFunc = nextPageFunc;

            return result;
        }

        public Task<FindHit<T>> FindOneAsync(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) {
            return FindOneAsync(query.Configure(), options.Configure());
        }

        public async Task<FindHit<T>> FindOneAsync(IRepositoryQuery query, ICommandOptions options = null) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var result = IsCacheEnabled ? await GetCachedQueryResultAsync<FindHit<T>>(options).AnyContext() : null;
            if (result != null)
                return result;

            options = ConfigureOptions(options);
            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = (await CreateSearchDescriptorAsync(query, options).AnyContext()).Size(1);
            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            _logger.Trace(() => response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return FindHit<T>.Empty;

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            result = response.Hits.FirstOrDefault()?.ToFindHit();
            if (IsCacheEnabled)
                await SetCachedQueryResultAsync(options, result).AnyContext();

            return result;
        }       

        public Task<FindResults<T>> SearchAsync(ISystemFilter systemFilter, string filter = null, string criteria = null, string sort = null, string aggregations = null, ICommandOptions options = null) {
            var search = NewQuery()
                .MergeFrom(systemFilter?.GetQuery())
                .FilterExpression(filter)
                .SearchExpression(criteria)
                .AggregationsExression(aggregations)
                .SortExpression(sort);

            return FindAsync(search, options);
        }

        public async Task<T> GetByIdAsync(Id id, ICommandOptions options = null) {
            if (String.IsNullOrEmpty(id.Value))
                return null;

            T hit = null;
            if (IsCacheEnabled && options.ShouldReadCache())
                hit = await Cache.GetAsync<T>(id, default(T)).AnyContext();

            if (hit != null) {
                _logger.Trace(() => $"Cache hit: type={ElasticType.Name} key={id}");
                return hit;
            }

            if (!HasParent || id.Routing != null) {
                var request = new GetRequest(GetIndexById(id), ElasticType.Name, id.Value);
                if (id.Routing != null)
                    request.Routing = id.Routing;
                var response = await _client.GetAsync<T>(request).AnyContext();
                _logger.Trace(() => response.GetRequest());

                hit = response.Found ? response.ToFindHit().Document : null;
            } else {
                // we don't have the parent id so we have to do a query
                // TODO: Ensure this is find one query is not cached.
                var findResult = await FindOneAsync(NewQuery().Id(id)).AnyContext();
                if (findResult != null)
                    hit = findResult.Document;
            }

            if (hit != null && IsCacheEnabled && options.ShouldUseCache())
                await Cache.SetAsync(id, hit, options.GetExpiresIn()).AnyContext();

            return hit;
        }

        public async Task<IReadOnlyCollection<T>> GetByIdsAsync(Ids ids, ICommandOptions options = null) {
            var idList = ids?.Distinct().Where(i => !String.IsNullOrEmpty(i)).ToList();
            if (idList == null || idList.Count == 0)
                return EmptyList;

            if (!HasIdentity)
                throw new NotSupportedException("Model type must implement IIdentity.");

            var hits = new List<T>();
            if (IsCacheEnabled && options.ShouldReadCache()) {
                var cacheHits = await Cache.GetAllAsync<T>(idList.Select(id => id.Value)).AnyContext();
                hits.AddRange(cacheHits.Where(kvp => kvp.Value.HasValue).Select(kvp => kvp.Value.Value));
            }

            var itemsToFind = idList.Except(hits.OfType<IIdentity>().Select(i => (Id)i.Id)).ToList();
            if (itemsToFind.Count == 0)
                return hits.AsReadOnly();

            var multiGet = new MultiGetDescriptor();
            foreach (var id in itemsToFind.Where(i => i.Routing != null || !HasParent)) {
                multiGet.Get<T>(f => {
                    f.Id(id.Value).Index(GetIndexById(id)).Type(ElasticType.Name);
                    if (id.Routing != null)
                        f.Routing(id.Routing);

                    return f;
                });
            }

            var multiGetResults = await _client.MultiGetAsync(multiGet).AnyContext();
            _logger.Trace(() => multiGetResults.GetRequest());

            foreach (var doc in multiGetResults.Documents) {
                if (!doc.Found)
                    continue;

                hits.Add(((IMultiGetHit<T>)doc).ToFindHit().Document);
                itemsToFind.Remove(doc.Id);
            }

            // fallback to doing a find
            if (itemsToFind.Count > 0 && (HasParent || HasMultipleIndexes)) {
                var response = await FindAsync(q => q.Id(itemsToFind.Select(id => id.Value)), o => o.PageLimit(1000)).AnyContext();
                do {
                    if (response.Hits.Count > 0)
                        hits.AddRange(response.Hits.Where(h => h.Document != null).Select(h => h.Document));
                } while (await response.NextPageAsync().AnyContext());
            }

            if (IsCacheEnabled && options.ShouldUseCache()) {
                foreach (var item in hits.OfType<IIdentity>())
                    await Cache.SetAsync(item.Id, item, options.GetExpiresIn()).AnyContext();
            }

            return hits.AsReadOnly();
        }

        public Task<FindResults<T>> GetAllAsync(ICommandOptions options = null) {
            return FindAsync(null, options);
        }

        public async Task<bool> ExistsAsync(Id id) {
            if (String.IsNullOrEmpty(id.Value))
                return false;

            if (!HasParent || id.Routing != null) {
                var response = await _client.DocumentExistsAsync<T>(new DocumentPath<T>(id.Value), d => {
                    d.Index(GetIndexById(id));
                    if (id.Routing != null)
                        d.Routing(id.Routing);

                    return d;
                }).AnyContext();
                _logger.Trace(() => response.GetRequest());

                return response.Exists;
            } else {
                return await ExistsAsync(q => q.Id(id)).AnyContext();
            }
        }

        public Task<bool> ExistsAsync(RepositoryQueryDescriptor<T> query) {
            return ExistsAsync(query.Configure());
        }

        public async Task<bool> ExistsAsync(IRepositoryQuery query) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var options = ConfigureOptions(null);
            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = (await CreateSearchDescriptorAsync(query, options).AnyContext()).Size(1);
            searchDescriptor.DocvalueFields(_idField);
            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            _logger.Trace(() => response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return false;

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            return response.HitsMetaData.Total > 0;
        }

        public Task<CountResult> CountAsync(RepositoryQueryDescriptor<T> query, CommandOptionsDescriptor<T> options = null) {
            return CountAsync(query.Configure(), options.Configure());
        }

        public async Task<CountResult> CountAsync(IRepositoryQuery query, ICommandOptions options = null) {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var result = await GetCachedQueryResultAsync<CountResult>(options, "count").AnyContext();
            if (result != null)
                return result;

            options = ConfigureOptions(options);
            await OnBeforeQueryAsync(query, options, typeof(T)).AnyContext();

            var searchDescriptor = await CreateSearchDescriptorAsync(query, options).AnyContext();
            searchDescriptor.Size(0);

            var response = await _client.SearchAsync<T>(searchDescriptor).AnyContext();
            _logger.Trace(() => response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return new CountResult();

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            result = new CountResult(response.Total, response.ToAggregations());
            await SetCachedQueryResultAsync(options, result, "count").AnyContext();
            return result;
        }

        public async Task<long> CountAsync(ICommandOptions options = null) {
            var response = await _client.CountAsync<T>(c => c.Query(q => q.MatchAll()).Index(String.Join(",", GetIndexesByQuery(null))).Type(ElasticType.Name)).AnyContext();
            _logger.Trace(() => response.GetRequest());

            if (!response.IsValid) {
                if (response.ApiCall.HttpStatusCode.GetValueOrDefault() == 404)
                    return 0;

                string message = response.GetErrorMessage();
                _logger.Error().Exception(response.OriginalException).Message(message).Property("request", response.GetRequest()).Write();
                throw new ApplicationException(message, response.OriginalException);
            }

            return response.Count;
        }

        public Task<CountResult> CountBySearchAsync(ISystemFilter systemFilter, string filter = null, string aggregations = null, ICommandOptions options = null) {
            var search = NewQuery()
                .MergeFrom(systemFilter?.GetQuery())
                .FilterExpression(filter)
                .AggregationsExression(aggregations);

            return CountAsync(search, options);
        }

        protected virtual IRepositoryQuery<T> NewQuery() {
            return new RepositoryQuery<T>();
        }

        protected virtual IRepositoryQuery ConfigureQuery(IRepositoryQuery query) {
            if (query == null)
                query = new RepositoryQuery<T>();

            if (DefaultExcludes.Count > 0 && query.GetExcludes().Count == 0)
                query.Exclude(DefaultExcludes);

            return query;
        }

        public bool IsCacheEnabled { get; private set; } = true;
        protected ScopedCacheClient Cache => _scopedCacheClient ?? new ScopedCacheClient(new NullCacheClient());

        private void SetCache(ICacheClient cache) {
            IsCacheEnabled = cache != null;
            _scopedCacheClient = new ScopedCacheClient(cache ?? new NullCacheClient(), EntityTypeName);
        }

        protected void DisableCache() {
            IsCacheEnabled = false;
            _scopedCacheClient = new ScopedCacheClient(new NullCacheClient(), EntityTypeName);
        }

        protected virtual async Task InvalidateCacheAsync(IReadOnlyCollection<ModifiedDocument<T>> documents, ICommandOptions options) {
            if (!IsCacheEnabled)
                return;

            if (documents != null && documents.Count > 0 && HasIdentity) {
                var keys = documents
                    .Select(d => d.Value)
                    .Cast<IIdentity>()
                    .Select(d => d.Id)
                    .ToList();

                if (keys.Count > 0)
                    await Cache.RemoveAllAsync(keys).AnyContext();
            }
        }

        public Task InvalidateCacheAsync(T document, ICommandOptions options = null) {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            if (!IsCacheEnabled)
                return TaskHelper.Completed();

            return InvalidateCacheAsync(new[] { document }, options);
        }

        public Task InvalidateCacheAsync(IEnumerable<T> documents, ICommandOptions options = null) {
            var docs = documents?.ToList();
            if (docs == null || docs.Any(d => d == null))
                throw new ArgumentNullException(nameof(documents));

            if (!IsCacheEnabled)
                return TaskHelper.Completed();

            return InvalidateCacheAsync(docs.Select(d => new ModifiedDocument<T>(d, null)).ToList(), options);
        }

        protected Task<SearchDescriptor<T>> CreateSearchDescriptorAsync(IRepositoryQuery query, ICommandOptions options) {
            return ConfigureSearchDescriptorAsync(null, query, options);
        }

        protected async Task<SearchDescriptor<T>> ConfigureSearchDescriptorAsync(SearchDescriptor<T> search, IRepositoryQuery query, ICommandOptions options) {
            if (search == null)
                search = new SearchDescriptor<T>();

            query = ConfigureQuery(query);
            search.Type(ElasticType.Name);
            var indices = GetIndexesByQuery(query);
            if (indices?.Length > 0)
                search.Index(String.Join(",", indices));
            if (HasVersion)
                search.Version(HasVersion);

            search.IgnoreUnavailable();

            await ElasticType.QueryBuilder.ConfigureSearchAsync(query, options, search).AnyContext();

            return search;
        }

        protected ICommandOptions ConfigureOptions(ICommandOptions options) {
            if (options == null)
                options = new CommandOptions<T>();

            options.ElasticType(ElasticType);

            return options;
        }

        protected string[] GetIndexesByQuery(IRepositoryQuery query, ICommandOptions options = null) {
            return HasMultipleIndexes ? TimeSeriesType.GetIndexesByQuery(query) : new[] { ElasticIndex.Name };
        }

        protected string GetIndexById(Id id) {
            return HasMultipleIndexes ? TimeSeriesType.GetIndexById(id) : ElasticIndex.Name;
        }

        protected Func<T, string> GetParentIdFunc => HasParent ? d => ChildType.GetParentId(d) : (Func<T, string>)null;
        protected Func<T, string> GetDocumentIndexFunc => HasMultipleIndexes ? d => TimeSeriesType.GetDocumentIndex(d) : (Func<T, string>)(d => ElasticIndex.Name);

        protected async Task<TResult> GetCachedQueryResultAsync<TResult>(ICommandOptions options, string cachePrefix = null, string cacheSuffix = null) {
            if (!IsCacheEnabled || options == null || !options.ShouldReadCache() || !options.HasCacheKey())
                return default(TResult);

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + options.GetCacheKey() : options.GetCacheKey();
            if (!String.IsNullOrEmpty(cacheSuffix))
                cacheKey += ":" + cacheSuffix;

            var result = await Cache.GetAsync<TResult>(cacheKey, default(TResult)).AnyContext();
            _logger.Trace(() => $"Cache {(result != null ? "hit" : "miss")}: type={ElasticType.Name} key={cacheKey}");

            return result;
        }

        protected async Task SetCachedQueryResultAsync<TResult>(ICommandOptions options, TResult result, string cachePrefix = null, string cacheSuffix = null) {
            if (!IsCacheEnabled || result == null || options == null || !options.ShouldUseCache() || !options.HasCacheKey())
                return;

            string cacheKey = cachePrefix != null ? cachePrefix + ":" + options.GetCacheKey() : options.GetCacheKey();
            if (!String.IsNullOrEmpty(cacheSuffix))
                cacheKey += ":" + cacheSuffix;

            await Cache.SetAsync(cacheKey, result, options.GetExpiresIn()).AnyContext();
            _logger.Trace(() => $"Set cache: type={ElasticType.Name} key={cacheKey}");
        }

        #region Elastic Type Configuration

        protected IIndex ElasticIndex => ElasticType.Index;

        private IIndexType<T> _elasticType;

        protected IIndexType<T> ElasticType {
            get { return _elasticType; }
            private set {
                _elasticType = value;

                if (_elasticType is IChildIndexType<T>) {
                    HasParent = true;
                    ChildType = _elasticType as IChildIndexType<T>;
                } else {
                    HasParent = false;
                    ChildType = null;
                }

                if (_elasticType is ITimeSeriesIndexType) {
                    HasMultipleIndexes = true;
                    TimeSeriesType = _elasticType as ITimeSeriesIndexType<T>;
                } else {
                    HasMultipleIndexes = false;
                    TimeSeriesType = null;
                }
            }
        }

        protected bool HasParent { get; private set; }
        protected IChildIndexType<T> ChildType { get; private set; }
        protected bool HasMultipleIndexes { get; private set; }
        protected ITimeSeriesIndexType<T> TimeSeriesType { get; private set; }

        #endregion

        #region Events

        public AsyncEvent<BeforeQueryEventArgs<T>> BeforeQuery { get; } = new AsyncEvent<BeforeQueryEventArgs<T>>();

        private async Task OnBeforeQueryAsync(IRepositoryQuery query, ICommandOptions options, Type resultType) {
            if (SupportsSoftDeletes && IsCacheEnabled) {
                var deletedIds = await Cache.GetSetAsync<string>("deleted").AnyContext();
                if (deletedIds.HasValue)
                    query.ExcludedId(deletedIds.Value);
            }

            if (BeforeQuery == null)
                return;

            await BeforeQuery.InvokeAsync(this, new BeforeQueryEventArgs<T>(query, options, this, resultType)).AnyContext();
        }

        #endregion
    }
}
