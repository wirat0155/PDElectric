using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EmissiView.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MDBController : ControllerBase
    {
        private readonly string _dataFolder;
        private readonly string _logFile;
        private readonly string _consumptionFile;

        public MDBController()
        {
            _dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "data");
            _logFile = Path.Combine(_dataFolder, "energy_log.json");
            _consumptionFile = Path.Combine(_dataFolder, "consumption.json");

            // Ensure data folder exists
            if (!Directory.Exists(_dataFolder))
            {
                Directory.CreateDirectory(_dataFolder);
            }
        }

        // Plant mapping based on MDB number
        private string GetPlantFromMDB(string mdb)
        {
            return mdb switch
            {
                "08" => "brazing",
                "09" => "brazing",
                "10" => "brazing",
                "01" => "lp",
                "02" => "lp",
                "03" => "plating",
                "04" => "plating",
                "05" => "plating",
                "06" => "plating",
                "07" => "plating",
                _ => "unknown"
            };
        }

        [HttpPost("ReceiveData")]
        public IActionResult ReceiveData([FromBody] MDBDataModel model)
        {
            if (model == null)
            {
                return BadRequest(new { message = "Invalid data received" });
            }

            try
            {
                var plant = GetPlantFromMDB(model.MDB);
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(model.Timestamp).DateTime;
                var dateKey = timestamp.ToString("yyyy-MM-dd");
                var timeKey = timestamp.ToString("HH:mm:ss");

                // 1. Save raw log entry
                var logEntry = new
                {
                    MDB = model.MDB,
                    Plant = plant,
                    kWh = model.kWh,
                    Wh = model.Wh,
                    Status = model.Status,
                    Timestamp = model.Timestamp,
                    DateTime = timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                    Date = model.Date,
                    Time = model.Time
                };

                var logData = new List<object>();
                if (System.IO.File.Exists(_logFile))
                {
                    var existingJson = System.IO.File.ReadAllText(_logFile);
                    if (!string.IsNullOrEmpty(existingJson))
                    {
                        logData = JsonSerializer.Deserialize<List<object>>(existingJson) ?? new List<object>();
                    }
                }
                logData.Add(logEntry);
                System.IO.File.WriteAllText(_logFile, JsonSerializer.Serialize(logData, new JsonSerializerOptions { WriteIndented = true }));

                // 2. Update consumption data - store first and last reading of each day
                // Structure: { plant: { "yyyy-MM-dd": { "firstWh": x, "lastWh": y, "firstTime": "HH:mm:ss", "lastTime": "HH:mm:ss" } } }
                var consumptionData = new Dictionary<string, Dictionary<string, DailyReading>>();
                if (System.IO.File.Exists(_consumptionFile))
                {
                    var existingJson = System.IO.File.ReadAllText(_consumptionFile);
                    if (!string.IsNullOrEmpty(existingJson))
                    {
                        consumptionData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DailyReading>>>(existingJson) ?? new Dictionary<string, Dictionary<string, DailyReading>>();
                    }
                }

                if (!consumptionData.ContainsKey(plant))
                {
                    consumptionData[plant] = new Dictionary<string, DailyReading>();
                }

                // Update first/last reading for the day
                if (!consumptionData[plant].ContainsKey(dateKey))
                {
                    consumptionData[plant][dateKey] = new DailyReading
                    {
                        FirstWh = model.Wh,
                        LastWh = model.Wh,
                        FirstTime = timeKey,
                        LastTime = timeKey
                    };
                }
                else
                {
                    var reading = consumptionData[plant][dateKey];
                    
                    // Handle reset detection: if new reading is significantly lower than last reading
                    // and the time gap is reasonable (not a new day), treat as reset
                    bool isReset = model.Wh < reading.LastWh && (reading.LastWh - model.Wh) < 10000000; // Threshold: 10M Wh
                    
                    if (isReset)
                    {
                        // Reset detected: treat current reading as new first reading
                        // The consumption for this day will be calculated as LastWh (before reset)
                        // Next readings will start from this new baseline
                        reading.FirstWh = model.Wh;
                        reading.FirstTime = timeKey;
                        reading.LastWh = model.Wh;
                        reading.LastTime = timeKey;
                    }
                    else
                    {
                        // Normal case: update first/last reading
                        // Update first reading if this is earlier in the day
                        if (string.Compare(timeKey, reading.FirstTime) < 0)
                        {
                            reading.FirstWh = model.Wh;
                            reading.FirstTime = timeKey;
                        }
                        // Update last reading if this is later in the day
                        if (string.Compare(timeKey, reading.LastTime) > 0)
                        {
                            reading.LastWh = model.Wh;
                            reading.LastTime = timeKey;
                        }
                    }
                }

                System.IO.File.WriteAllText(_consumptionFile, JsonSerializer.Serialize(consumptionData, new JsonSerializerOptions { WriteIndented = true }));

                return Ok(new
                {
                    success = true,
                    received = new
                    {
                        MDB = model.MDB,
                        Plant = plant,
                        kWh = model.kWh,
                        Wh = model.Wh,
                        Status = model.Status,
                        Timestamp = model.Timestamp,
                        Date = model.Date,
                        Time = model.Time,
                        Datetime = model.Datetime
                    },
                    message = "Data received and saved successfully"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Error saving data: {ex.Message}" });
            }
        }

        // API to get daily totals for modal chart (Daily kWh = LastWh - FirstWh, handle resets)
        [HttpGet("GetDailyTotals")]
        public IActionResult GetDailyTotals([FromQuery] int year, [FromQuery] int month)
        {
            try
            {
                var consumptionFile = Path.Combine(_dataFolder, "consumption.json");
                var dailyTotals = new Dictionary<string, Dictionary<string, double>>();
                var targetMonthPrefix = $"{year}-{month:D2}";

                if (System.IO.File.Exists(consumptionFile))
                {
                    var json = System.IO.File.ReadAllText(consumptionFile);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var consumptionData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DailyReading>>>(json);
                        if (consumptionData != null)
                        {
                            foreach (var plant in consumptionData.Keys)
                            {
                                dailyTotals[plant] = new Dictionary<string, double>();
                                foreach (var (date, reading) in consumptionData[plant])
                                {
                                    if (date.StartsWith(targetMonthPrefix))
                                    {
                                        // Handle reset: if LastWh < FirstWh, use LastWh as consumption
                                        // Otherwise: Daily consumption = LastWh - FirstWh
                                        long consumptionWh;
                                        if (reading.LastWh < reading.FirstWh)
                                        {
                                            // Reset occurred, use LastWh as the new starting point
                                            consumptionWh = reading.LastWh;
                                        }
                                        else
                                        {
                                            consumptionWh = reading.LastWh - reading.FirstWh;
                                        }
                                        var dailyKwh = consumptionWh / 1000.0;
                                        dailyTotals[plant][date] = Math.Round(dailyKwh, 3);
                                    }
                                }
                            }
                        }
                    }
                }

                return Ok(new { dailyTotals });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Error reading data: {ex.Message}" });
            }
        }

        // API to get monthly totals for secondary charts (Monthly kWh = Sum of daily consumptions, handle resets)
        [HttpGet("GetMonthlyTotals")]
        public IActionResult GetMonthlyTotals([FromQuery] int year)
        {
            try
            {
                var consumptionFile = Path.Combine(_dataFolder, "consumption.json");
                var dailyTotals = new Dictionary<string, Dictionary<string, double>>();
                var targetYearPrefix = $"{year}-";

                if (System.IO.File.Exists(consumptionFile))
                {
                    var json = System.IO.File.ReadAllText(consumptionFile);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var consumptionData = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, DailyReading>>>(json);
                        if (consumptionData != null)
                        {
                            foreach (var plant in consumptionData.Keys)
                            {
                                dailyTotals[plant] = new Dictionary<string, double>();
                                foreach (var (date, reading) in consumptionData[plant])
                                {
                                    if (date.StartsWith(targetYearPrefix))
                                    {
                                        // Handle reset: if LastWh < FirstWh, use LastWh as consumption
                                        long consumptionWh;
                                        if (reading.LastWh < reading.FirstWh)
                                        {
                                            consumptionWh = reading.LastWh;
                                        }
                                        else
                                        {
                                            consumptionWh = reading.LastWh - reading.FirstWh;
                                        }
                                        var dailyKwh = consumptionWh / 1000.0;
                                        dailyTotals[plant][date] = Math.Round(dailyKwh, 3);
                                    }
                                }
                            }
                        }
                    }
                }

                return Ok(new { dailyTotals });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Error reading data: {ex.Message}" });
            }
        }
    }

    public class MDBDataModel
    {
        public string MDB { get; set; }
        public double kWh { get; set; }
        public long Wh { get; set; }
        public string Status { get; set; }
        public long Timestamp { get; set; }
        public string Date { get; set; }
        public string Time { get; set; }
        public string Datetime { get; set; }
    }

    public class DailyReading
    {
        public long FirstWh { get; set; }
        public long LastWh { get; set; }
        public string FirstTime { get; set; }
        public string LastTime { get; set; }
    }
}
