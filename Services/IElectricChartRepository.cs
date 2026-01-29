using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PDElectric.Models;

namespace PDElectric.Services
{
    public interface IElectricChartRepository
    {
        Task<IEnumerable<ProductionDataModel>> GetProductionDataAsync(DateTime startDate, DateTime endDate);
    }
}
