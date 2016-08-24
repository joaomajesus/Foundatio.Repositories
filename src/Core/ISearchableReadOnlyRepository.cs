﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Foundatio.Repositories.Models;
using Foundatio.Repositories.Queries;

namespace Foundatio.Repositories {
    public interface ISearchableReadOnlyRepository<T> : IReadOnlyRepository<T> where T : class, new() {
        Task<CountResult> CountBySearchAsync(object systemFilter, string userFilter = null, string query = null, AggregationOptions aggregations = null);
        Task<IFindResults<T>> SearchAsync(object systemFilter, string userFilter = null, string query = null, SortingOptions sorting = null, PagingOptions paging = null, AggregationOptions aggregations = null);
        Task<IReadOnlyCollection<AggregationResult>> GetAggregationsAsync(object systemFilter, AggregationOptions aggregations, string userFilter = null, string query = null);
    }
}