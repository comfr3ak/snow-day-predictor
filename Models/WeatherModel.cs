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

        // Derived regional impact from latitude.
        // 1.0 = baseline, >1.0 = lower-latitude (less tolerant), <1.0 = higher-latitude (more tolerant)
        public double LatitudeImpactFactor => Latitude switch
        {
            < 28.0 => 2.00,  // South FL / far South TX
            < 30.0 => 1.85,  // Gulf Coast band
            < 32.5 => 1.65,  // Deep South band
            < 35.0 => 1.45,  // Southern tier
            < 38.0 => 1.25,  // VA/MD-ish
            < 41.0 => 1.05,  // Mid band
            < 44.0 => 0.95,  // Great Lakes / upstate
            < 47.0 => 0.85,  // Northern tier
            _ => 0.75        // far north
        };

        public int SnowToleranceMultiplier
        {
            get
            {
                // Lower preparedness => multiplier > 100 (more closure prone)
                // Higher preparedness => multiplier < 100 (less closure prone)
                // Range roughly 60..200
                double p = PreparednessIndex;           // 0..1
                double multiplier = 200 - (140 * p);    // p=0 ->200, p=1 ->60
                return (int)Math.Round(multiplier);
            }
        }

        public int TypicalClosureDays
        {
            get
            {
                // Recovery is slower in low preparedness areas, faster in high preparedness areas
                double p = PreparednessIndex;
                if (p < 0.20) return 4;   // deep south-like
                if (p < 0.40) return 3;   // southern tier
                if (p < 0.65) return 3;   // transition zones (VA often behaves like this for big storms)
                if (p < 0.85) return 2;   // northern mid-atlantic / midwest
                return 1;                 // far north
            }
        }


        public double PreparednessIndex
        {
            get
            {
                // Typical US snow preparedness rises with latitude.
                // 0.0 = very unprepared, 1.0 = very prepared.
                // Center around ~40.5 (roughly Mid-Atlantic transition), span ~6 degrees.
                double x = (Latitude - 40.5) / 6.0;

                // Smoothstep-ish sigmoid
                double sigmoid = 1.0 / (1.0 + Math.Exp(-x * 2.2));

                // Clamp
                if (sigmoid < 0) sigmoid = 0;
                if (sigmoid > 1) sigmoid = 1;
                return sigmoid;
            }
        }



        // Optional helpers you can use elsewhere if you want ice to weigh more than snow at low latitudes.
        // These are not required by your current code, but they are useful for future tuning.
        public double IceAmplifier => 1.0 + System.Math.Max(0.0, LatitudeImpactFactor - 1.0) * 0.60;
        public double SnowAmplifier => 1.0 + System.Math.Max(0.0, LatitudeImpactFactor - 1.0) * 0.30;

        // Keep compatibility with existing imports:
        // using static SnowDayPredictor.Models.GeographyContext;
        public class HistoricalWeatherDay
        {
            public System.DateTime Date { get; set; }
            public double SnowfallInches { get; set; }
            public double TempMax { get; set; }
            public double TempMin { get; set; }
            public double Precipitation { get; set; }
        }

        public class OpenMeteoHistoricalResponse
        {
            [System.Text.Json.Serialization.JsonPropertyName("daily")]
            public DailyData? Daily { get; set; }
        }

        public class DailyData
        {
            [System.Text.Json.Serialization.JsonPropertyName("time")]
            public System.Collections.Generic.List<string> Time { get; set; } = new();

            [System.Text.Json.Serialization.JsonPropertyName("snowfall_sum")]
            public System.Collections.Generic.List<double> SnowfallSum { get; set; } = new();

            [System.Text.Json.Serialization.JsonPropertyName("temperature_2m_max")]
            public System.Collections.Generic.List<double> TempMax { get; set; } = new();

            [System.Text.Json.Serialization.JsonPropertyName("temperature_2m_min")]
            public System.Collections.Generic.List<double> TempMin { get; set; } = new();

            [System.Text.Json.Serialization.JsonPropertyName("precipitation_sum")]
            public System.Collections.Generic.List<double> PrecipitationSum { get; set; } = new();
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