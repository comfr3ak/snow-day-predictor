namespace SnowDayPredictor.Models
{
    // For geocoding ZIP to coordinates
    public class GeocodingResponse
    {
        public double Lat { get; set; }
        public double Lon { get; set; }
    }

    // NWS API Points response
    public class NWSPointsResponse
    {
        public PointsProperties Properties { get; set; } = new();
    }

    public class PointsProperties
    {
        public string Forecast { get; set; } = "";
        public string ForecastHourly { get; set; } = "";
        public RelativeLocation RelativeLocation { get; set; } = new();
    }

    public class RelativeLocation
    {
        public RelativeLocationProperties Properties { get; set; } = new();
    }

    public class RelativeLocationProperties
    {
        public string City { get; set; } = "";
        public string State { get; set; } = "";
    }

    // NWS Forecast response
    public class NWSForecastResponse
    {
        public ForecastProperties Properties { get; set; } = new();
    }

    public class ForecastProperties
    {
        public List<Period> Periods { get; set; } = new();
    }

    public class Period
    {
        public int Number { get; set; }
        public string Name { get; set; } = "";
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public bool IsDaytime { get; set; }
        public int Temperature { get; set; }
        public string TemperatureUnit { get; set; } = "";
        public string WindSpeed { get; set; } = "";
        public string WindDirection { get; set; } = "";
        public string ShortForecast { get; set; } = "";
        public string DetailedForecast { get; set; } = "";
        public ProbabilityOfPrecipitation? ProbabilityOfPrecipitation { get; set; }
        public QuantitativeValue? SnowfallAmount { get; set; }
        public QuantitativeValue? IceAccumulation { get; set; }
    }

    public class ProbabilityOfPrecipitation
    {
        public string UnitCode { get; set; } = "";
        public int? Value { get; set; }
    }

    public class QuantitativeValue
    {
        public string UnitCode { get; set; } = "";
        public double? Value { get; set; }
    }

    // Climate data from Cloudflare Worker
    public class ClimateDataResponse
    {
        public double AvgAnnualSnowfall { get; set; }
        public string Source { get; set; } = "";
        public string? StationId { get; set; }
        public string? StationName { get; set; }
        public int DataYears { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    // Historical weather data
    public class HistoricalWeatherDay
    {
        public DateTime Date { get; set; }
        public double SnowfallInches { get; set; }
        public double TempMax { get; set; }
        public double TempMin { get; set; }
        public double Precipitation { get; set; }
    }

    // Open-Meteo Historical API Response
    public class OpenMeteoHistoricalResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("daily")]
        public DailyData? Daily { get; set; }
    }

    public class DailyData
    {
        [System.Text.Json.Serialization.JsonPropertyName("time")]
        public List<string> Time { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("snowfall_sum")]
        public List<double> SnowfallSum { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("temperature_2m_max")]
        public List<double> TempMax { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("temperature_2m_min")]
        public List<double> TempMin { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("precipitation_sum")]
        public List<double> PrecipitationSum { get; set; } = new();
    }

    // Geography context with climate-based preparedness
    public class GeographyContext
    {
        public string State { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double AvgAnnualSnowfall { get; set; }

        /// <summary>
        /// Snow preparedness index based on average annual snowfall.
        /// 0.0 = unprepared (rarely snows), 1.0 = very prepared (regular snow)
        /// Uses sigmoid curve centered at 30 inches
        /// </summary>
        public double PreparednessIndex
        {
            get
            {
                // Sigmoid: 1 / (1 + e^(-0.1 * (snowfall - 30)))
                // 0" -> ~0.05
                // 15" -> ~0.18
                // 30" -> ~0.50
                // 60" -> ~0.95
                // 100" -> ~0.9999
                double x = AvgAnnualSnowfall - 30.0;
                double sigmoid = 1.0 / (1.0 + Math.Exp(-0.1 * x));
                return Math.Max(0.0, Math.Min(1.0, sigmoid));
            }
        }

        /// <summary>
        /// How many inches of snow typically trigger closures.
        /// Lower preparedness = lower threshold
        /// </summary>
        public double ClosureThresholdInches
        {
            get
            {
                // Unprepared (0.0): 1" triggers closures
                // Mid-prepared (0.5): 3" triggers closures
                // Very prepared (1.0): 6" triggers closures
                return 1.0 + (PreparednessIndex * 5.0);
            }
        }

        /// <summary>
        /// How many days schools typically stay closed after a snow event.
        /// Lower preparedness = longer closures
        /// </summary>
        public int TypicalClosureDays
        {
            get
            {
                double p = PreparednessIndex;
                if (p < 0.20) return 4;  // Deep south: 4+ days
                if (p < 0.40) return 3;  // Southern tier: 3 days
                if (p < 0.65) return 2;  // Mid-Atlantic: 2 days
                if (p < 0.85) return 2;  // Northern mid-tier: 2 days
                return 1;                // Far north: 1 day
            }
        }

        /// <summary>
        /// Daily decay rate for aftermath probabilities.
        /// Higher preparedness = faster recovery
        /// </summary>
        public double AftermathDecayRate
        {
            get
            {
                // 0.0 prep -> 0.30 decay (slow recovery)
                // 0.5 prep -> 0.50 decay (moderate)
                // 1.0 prep -> 0.70 decay (fast recovery)
                return 0.30 + (PreparednessIndex * 0.40);
            }
        }

        /// <summary>
        /// Temperature-based melt factor. How fast does snow melt?
        /// Higher preparedness = better clearing even without melting
        /// </summary>
        public double GetMeltFactor(int tempF)
        {
            if (tempF > 40) return 0.50;  // Fast melt
            if (tempF > 32) return 0.30;  // Moderate melt
            if (tempF > 25) return 0.10;  // Slow melt
            return 0.0;                    // No melt, stays frozen
        }
    }

    // Our snow day calculation result
    public class SnowDayForecast
    {
        public string DayName { get; set; } = "";
        public DateTime Date { get; set; }
        public int SnowDayChance { get; set; }
        public int DelayChance { get; set; }
        public int Temperature { get; set; }
        public string Forecast { get; set; } = "";
        public string DetailedForecast { get; set; } = "";
        public int PrecipitationChance { get; set; }
        public string? SnowfallAmount { get; set; }
        public bool IsAftermathDay { get; set; }
        public int DaysSinceSnowEvent { get; set; }

        public string ChanceLevel => SnowDayChance switch
        {
            >= 70 => "High",
            >= 40 => "Moderate",
            >= 15 => "Low",
            _ => "Very Low"
        };

        public string DelayLevel => DelayChance switch
        {
            >= 60 => "High",
            >= 35 => "Moderate",
            >= 15 => "Low",
            _ => "Very Low"
        };

        public string ChanceColor => SnowDayChance switch
        {
            >= 70 => "#dc3545", // red
            >= 40 => "#ffc107", // yellow
            >= 15 => "#17a2b8", // blue
            _ => "#28a745"      // green
        };

        public string DelayColor => DelayChance switch
        {
            >= 60 => "#ff6b6b",
            >= 35 => "#ffd93d",
            >= 15 => "#6bcf7f",
            _ => "#95e1d3"
        };
    }

    // Saved location for user
    public class SavedLocation
    {
        public string ZipCode { get; set; } = "";
        public string CityName { get; set; } = "";
        public DateTime LastUsed { get; set; }
    }

    // NWS Weather Alerts
    public class NWSAlertsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("features")]
        public List<AlertFeature> Features { get; set; } = new();
    }

    public class AlertFeature
    {
        [System.Text.Json.Serialization.JsonPropertyName("properties")]
        public AlertProperties Properties { get; set; } = new();
    }

    public class AlertProperties
    {
        [System.Text.Json.Serialization.JsonPropertyName("event")]
        public string Event { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("headline")]
        public string Headline { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("description")]
        public string Description { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("severity")]
        public string Severity { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("urgency")]
        public string Urgency { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("onset")]
        public DateTime? Onset { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expires")]
        public DateTime? Expires { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("ends")]
        public DateTime? Ends { get; set; }
    }

    public class WeatherAlert
    {
        public string Type { get; set; } = "";
        public string Headline { get; set; } = "";
        public string Description { get; set; } = "";
        public AlertSeverity Severity { get; set; }
        public DateTime? Onset { get; set; }
        public DateTime? Expires { get; set; }
        public DateTime? Ends { get; set; }
    }

    public enum AlertSeverity
    {
        None = 0,
        Advisory = 1,   // Winter Weather Advisory: +15-25%
        Watch = 2,      // Winter Storm Watch: +20-30%
        Warning = 3,    // Winter Storm Warning: +30-50%
        Extreme = 4     // Blizzard/Ice Storm Warning: +50-70%
    }
}
