using System;

namespace PDElectric.Models
{
    public class ProductionDataModel
    {
        public DateTime TranDate { get; set; }
        public decimal TranQty { get; set; }
        public string Plant { get; set; }
    }
}
