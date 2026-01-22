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

    // Our snow day calculation result
    public class SnowDayForecast
    {
        public string DayName { get; set; } = "";
        public DateTime Date { get; set; }
        public int SnowDayChance { get; set; }
        public int DelayChance { get; set; }  // NEW
        public int Temperature { get; set; }
        public string Forecast { get; set; } = "";
        public int PrecipitationChance { get; set; }
        public string? SnowfallAmount { get; set; }
        public string State { get; set; } = "";  // NEW - for geography adjustments

        public string ChanceLevel => SnowDayChance switch
        {
            >= 70 => "High",
            >= 40 => "Moderate",
            >= 15 => "Low",
            _ => "Very Low"
        };

        public string DelayLevel => DelayChance switch  // NEW
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

        public string DelayColor => DelayChance switch  // NEW
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

    public class QuantitativeValue
    {
        public string UnitCode { get; set; } = "";
        public double? Value { get; set; }
    }

    public class GeographyContext
    {
        public string State { get; set; } = "";
        public double Latitude { get; set; }

        // Snow tolerance based on state/region
        // Snow tolerance based on region
        public int SnowToleranceMultiplier => State.ToUpper() switch
        {
            // Deep South - very low tolerance
            "FL" or "GA" or "SC" or "AL" or "MS" or "LA" or "TX" or "AR" => 200,

            // Mid-South + DC Metro - low tolerance, extended recovery
            "NC" or "TN" or "KY" or "VA" or "MD" or "WV" or "OK" => 150,

            // Mid-Atlantic/Lower Midwest - moderate tolerance
            "DE" or "PA" or "MO" or "KS" => 100,

            // Southern portions of these states
            "OH" or "IN" => 100,

            // Northeast Corridor - high tolerance
            "NJ" or "NY" or "CT" or "MA" or "RI" => 75,

            // Northern/Mountain states - very high tolerance
            "VT" or "NH" or "ME" or "MI" or "WI" or "IL" or "IA" or "MN" => 50,
            "MT" or "WY" or "CO" or "ID" or "ND" or "SD" or "AK" or "UT" => 50,

            // West Coast (rare snow, but cities struggle)
            "WA" or "OR" or "CA" => 150,
            "NV" or "AZ" or "NM" => 175,

            _ => 100 // Default
        };

        // How many days closures typically persist
        public int TypicalClosureDays => State.ToUpper() switch
        {
            "FL" or "GA" or "SC" or "AL" or "MS" or "LA" or "TX" or "AR" => 3,
            "NC" or "TN" or "KY" or "VA" or "WV" => 2,
            _ => 1
        };

        // Historical weather data
        public class HistoricalWeatherDay
        {
            public DateTime Date { get; set; }
            public double SnowfallInches { get; set; }
            public double TempMax { get; set; }
            public double TempMin { get; set; }
            public double Precipitation { get; set; }
        }

        // Open-Meteo API response models
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
        Advisory = 1,      // Winter Weather Advisory: +15-25%
        Watch = 2,         // Winter Storm Watch: +20-30%
        Warning = 3,       // Winter Storm Warning: +30-50%
        Extreme = 4        // Blizzard/Ice Storm Warning: +50-70%
    }
}