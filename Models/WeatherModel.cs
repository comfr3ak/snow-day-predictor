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
        public string Cwa { get; set; } = "";  // NWS Weather Forecast Office code (e.g., "MEG", "FFC")
        public string County { get; set; } = "";  // URL to county zone (e.g., "https://api.weather.gov/zones/county/NYC075")
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
        public string County { get; set; } = "";  // County name (e.g., "Oswego")
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
        /// Scale directly with annual snowfall for better accuracy.
        /// </summary>
        public double ClosureThresholdInches
        {
            get
            {
                // Scale threshold directly with annual snowfall
                // 0" annual → 1" triggers closures (no equipment, any snow causes panic)
                // 30" annual → 5" triggers closures (moderate equipment)
                // 60" annual → 14" triggers closures (well-equipped lake effect areas)
                // 100" annual → ~29" triggers closures (very well-equipped, like Buffalo)
                double baseThreshold = 1.0 + (AvgAnnualSnowfall / 7.5);

                // Ultra-high-prep areas (50"+ annual) need steeper threshold increase
                // These lake effect regions are so well-equipped they handle snow much better
                if (AvgAnnualSnowfall > 50)
                {
                    double extraThreshold = (AvgAnnualSnowfall - 50) * 0.5;
                    return baseThreshold + extraThreshold;
                }

                return baseThreshold;
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
        /// Proportional to degrees above freezing for more accurate modeling.
        /// </summary>
        public double GetMeltFactor(int tempF)
        {
            if (tempF <= 32) return 0.0;  // No melt at or below freezing

            // Proportional melt: 0.03 per degree above 32°F, capped at 0.60
            // 34°F → 0.06 (barely melting)
            // 40°F → 0.24 (moderate melt)
            // 45°F → 0.39 (significant melt)
            // 52°F+ → 0.60 (fast melt, capped)
            double degreesAbove = tempF - 32;
            return Math.Min(0.60, degreesAbove * 0.03);
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
        public string? Reasoning { get; set; }

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

    // IEM (Iowa Environmental Mesonet) Historical Alerts Response
    // Used to fetch past winter weather alerts when NWS active alerts have expired
    public class IEMAlertResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("features")]
        public List<IEMAlertFeature> Features { get; set; } = new();
    }

    public class IEMAlertFeature
    {
        [System.Text.Json.Serialization.JsonPropertyName("properties")]
        public IEMAlertProperties Properties { get; set; } = new();
    }

    public class IEMAlertProperties
    {
        [System.Text.Json.Serialization.JsonPropertyName("wfo")]
        public string Wfo { get; set; } = "";  // Weather Forecast Office code

        [System.Text.Json.Serialization.JsonPropertyName("phenomena")]
        public string Phenomena { get; set; } = "";  // WS=Winter Storm, WW=Winter Weather, BZ=Blizzard, IS=Ice Storm

        [System.Text.Json.Serialization.JsonPropertyName("significance")]
        public string Significance { get; set; } = "";  // W=Warning, A=Advisory, Y=Advisory

        [System.Text.Json.Serialization.JsonPropertyName("issue")]
        public DateTime? Issue { get; set; }  // When alert was issued

        [System.Text.Json.Serialization.JsonPropertyName("expire")]
        public DateTime? Expire { get; set; }  // When alert expires

        [System.Text.Json.Serialization.JsonPropertyName("product_issue")]
        public DateTime? ProductIssue { get; set; }
    }

    // NWS County Zone response (for getting county name from zone URL)
    public class NWSZoneResponse
    {
        public NWSZoneProperties Properties { get; set; } = new();
    }

    public class NWSZoneProperties
    {
        public string Name { get; set; } = "";  // County name (e.g., "Oswego")
        public string State { get; set; } = "";  // State abbreviation
    }

    // Closings API response models
    public class ClosingsResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("state")]
        public string State { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("county")]
        public string County { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("total")]
        public int Total { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("items")]
        public List<ClosingItem> Items { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("source")]
        public ClosingsSource? Source { get; set; }
    }

    public class ClosingItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("organization_name")]
        public string OrganizationName { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("organization_type")]
        public string OrganizationType { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("closure_type")]
        public string ClosureType { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("closure_status")]
        public string ClosureStatus { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("raw_status_text")]
        public string RawStatusText { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("effective_date")]
        public string EffectiveDate { get; set; } = "";
    }

    public class ClosingsSource
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("url")]
        public string Url { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("cachedMinutes")]
        public int CachedMinutes { get; set; }
    }
}
