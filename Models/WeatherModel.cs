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
        public int SnowToleranceMultiplier => State.ToUpper() switch
        {
            // Southern states - very low tolerance
            "FL" or "GA" or "SC" or "AL" or "MS" or "LA" or "TX" or "AR" => 200,

            // Mid-South - low tolerance
            "NC" or "TN" or "KY" or "VA" or "WV" or "OK" => 150,

            // Mid-Atlantic/Lower Midwest - moderate tolerance
            "MD" or "DE" or "NJ" or "PA" or "OH" or "IN" or "MO" or "KS" => 100,

            // Northeast/Upper Midwest - high tolerance
            "NY" or "CT" or "MA" or "RI" or "VT" or "NH" or "ME" or "MI" or "WI" or "IL" or "IA" or "MN" => 75,

            // Mountain/Northern states - very high tolerance
            "MT" or "WY" or "CO" or "ID" or "ND" or "SD" or "AK" or "UT" => 50,

            _ => 100 // Default
        };

        // How many days closures typically persist
        public int TypicalClosureDays => State.ToUpper() switch
        {
            "FL" or "GA" or "SC" or "AL" or "MS" or "LA" or "TX" or "AR" => 3,
            "NC" or "TN" or "KY" or "VA" or "WV" => 2,
            _ => 1
        };
    }
}