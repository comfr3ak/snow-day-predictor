using System.Net.Http.Json;
using System.Text.Json;
using SnowDayPredictor.Models;
using Microsoft.JSInterop;

namespace SnowDayPredictor.Services
{
    public class WeatherService
    {
        private readonly HttpClient _httpClient;
        private readonly IJSRuntime? _jsRuntime;
        private const string CloudflareWorkerUrl = "https://snow-day-climate-proxy.ncs-cee.workers.dev";
        private const string ClimateDataCacheKey = "climateDataCache";
        private const string HistoricalWeatherCacheKey = "historicalWeatherCache";
        private const string ZipLookupCacheKey = "zipLookupCache";

        public WeatherService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _jsRuntime = null;
        }

        public WeatherService(HttpClient httpClient, IJSRuntime jsRuntime)
        {
            _httpClient = httpClient;
            _jsRuntime = jsRuntime;
        }

        /// <summary>
        /// Get climate data and snow preparedness for a location (with localStorage caching)
        /// </summary>
        public async Task<GeographyContext?> GetClimateDataAsync(double latitude, double longitude)
        {
            var cacheKey = $"{latitude:F2},{longitude:F2}";

            // Try to get from localStorage cache first (if JSRuntime is available)
            if (_jsRuntime != null)
            {
                try
                {
                    var cachedJson = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", $"{ClimateDataCacheKey}_{cacheKey}");
                    if (!string.IsNullOrEmpty(cachedJson))
                    {
                        var cached = System.Text.Json.JsonSerializer.Deserialize<GeographyContext>(cachedJson);
                        if (cached != null)
                        {
                            Console.WriteLine($"Using cached climate data from localStorage for {cacheKey}");
                            return cached;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading climate cache from localStorage: {ex.Message}");
                }
            }

            // Not in cache or cache read failed - fetch from API
            try
            {
                var url = $"{CloudflareWorkerUrl}?lat={latitude}&lon={longitude}";
                var response = await _httpClient.GetFromJsonAsync<ClimateDataResponse>(url);

                if (response == null) return null;

                var result = new GeographyContext
                {
                    Latitude = response.Latitude,
                    Longitude = response.Longitude,
                    AvgAnnualSnowfall = response.AvgAnnualSnowfall,
                    State = "" // Will be populated from NWS
                };

                // Cache the result in localStorage (if JSRuntime is available)
                if (_jsRuntime != null)
                {
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(result);
                        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", $"{ClimateDataCacheKey}_{cacheKey}", json);
                        Console.WriteLine($"Cached climate data to localStorage for {cacheKey}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error caching climate data to localStorage: {ex.Message}");
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Climate data fetch failed: {ex.Message}");
                // Fallback: latitude-based estimation
                var fallback = new GeographyContext
                {
                    Latitude = latitude,
                    Longitude = longitude,
                    AvgAnnualSnowfall = EstimateSnowfallByLatitude(latitude),
                    State = ""
                };

                // Cache the fallback too
                if (_jsRuntime != null)
                {
                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Serialize(fallback);
                        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", $"{ClimateDataCacheKey}_{cacheKey}", json);
                    }
                    catch { }
                }

                return fallback;
            }
        }

        private double EstimateSnowfallByLatitude(double lat)
        {
            if (lat < 28) return 0;
            if (lat < 32) return 2;
            if (lat < 36) return 5;
            if (lat < 40) return 15;
            if (lat < 43) return 40;
            if (lat < 47) return 60;
            return 80;
        }

        /// <summary>
        /// Get coordinates AND city name from ZIP code with localStorage caching (permanent cache)
        /// Uses Zippopotam - no User-Agent required, works on iOS
        /// Returns everything in ONE API call instead of two separate calls
        /// </summary>
        public async Task<(double lat, double lon, string city, string state)?> GetCoordinatesAndCityFromZip(string zipCode)
        {
            // Try to get from localStorage cache first
            if (_jsRuntime != null)
            {
                try
                {
                    var cachedJson = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", $"{ZipLookupCacheKey}_{zipCode}");
                    if (!string.IsNullOrEmpty(cachedJson))
                    {
                        var cached = System.Text.Json.JsonSerializer.Deserialize<ZipLookupData>(cachedJson);
                        if (cached != null)
                        {
                            Console.WriteLine($"Using cached ZIP lookup for {zipCode}: {cached.City}, {cached.State} at {cached.Lat}, {cached.Lon}");
                            return (cached.Lat, cached.Lon, cached.City, cached.State);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading ZIP lookup cache: {ex.Message}");
                }
            }

            // Not in cache - fetch from API
            double lat = 0;
            double lon = 0;
            string city = "";
            string state = "";

            // Try Zippopotam.us (no User-Agent required, works on iOS)
            // Returns BOTH coordinates AND city name in ONE call
            try
            {
                Console.WriteLine($"Fetching location data for ZIP {zipCode} from Zippopotam...");
                var url = $"https://api.zippopotam.us/us/{zipCode}";

                var response = await _httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("places", out var places) && places.GetArrayLength() > 0)
                    {
                        var place = places[0];

                        // Get coordinates
                        if (place.TryGetProperty("latitude", out var latElement))
                            lat = double.Parse(latElement.GetString() ?? "0");

                        if (place.TryGetProperty("longitude", out var lonElement))
                            lon = double.Parse(lonElement.GetString() ?? "0");

                        // Get city name
                        if (place.TryGetProperty("place name", out var placeNameElement))
                            city = placeNameElement.GetString() ?? "";

                        // Get state abbreviation
                        if (place.TryGetProperty("state abbreviation", out var stateAbbrElement))
                            state = stateAbbrElement.GetString() ?? "";

                        if (lat != 0 && lon != 0 && !string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(state))
                        {
                            Console.WriteLine($"✓ Zippopotam success: {city}, {state} at {lat}, {lon}");

                            // Cache the result permanently in localStorage
                            if (_jsRuntime != null)
                            {
                                try
                                {
                                    var cacheData = new ZipLookupData
                                    {
                                        Lat = lat,
                                        Lon = lon,
                                        City = city,
                                        State = state
                                    };
                                    var json = System.Text.Json.JsonSerializer.Serialize(cacheData);
                                    await _jsRuntime.InvokeVoidAsync("localStorage.setItem", $"{ZipLookupCacheKey}_{zipCode}", json);
                                    Console.WriteLine($"Cached ZIP lookup data for {zipCode}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"Error caching ZIP lookup: {ex.Message}");
                                }
                            }

                            return (lat, lon, city, state);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Zippopotam error: {ex.Message}");
            }

            // Fallback: Use ZIP prefix to get state at minimum
            state = GetStateFromZipPrefix(zipCode);
            if (!string.IsNullOrEmpty(state))
            {
                Console.WriteLine($"✓ Using ZIP prefix fallback: {zipCode} → {state} (no coordinates available)");
                city = $"ZIP {zipCode}";

                // For fallback, we don't have coordinates, so return null
                // The calling code will handle this appropriately
                return null;
            }

            // Complete failure
            Console.WriteLine($"✗ All lookups failed for ZIP: {zipCode}");
            return null;
        }

        /// <summary>
        /// Get coordinates from ZIP code (wrapper for backward compatibility)
        /// </summary>
        public async Task<(double lat, double lon)?> GetCoordinatesFromZip(string zipCode)
        {
            var result = await GetCoordinatesAndCityFromZip(zipCode);
            return result.HasValue ? (result.Value.lat, result.Value.lon) : null;
        }

        /// <summary>
        /// Get city name from ZIP code (wrapper for backward compatibility)
        /// </summary>
        public async Task<(string city, string state)> GetCityNameFromZip(string zipCode)
        {
            var result = await GetCoordinatesAndCityFromZip(zipCode);
            if (result.HasValue)
            {
                return (result.Value.city, result.Value.state);
            }

            // Fallback to ZIP prefix if lookup failed
            var state = GetStateFromZipPrefix(zipCode);
            if (!string.IsNullOrEmpty(state))
            {
                return ($"ZIP {zipCode}", state);
            }

            return ($"ZIP {zipCode}", "");
        }

        // Add this helper class at the end of the WeatherService class (before the closing brace):
        private class ZipLookupData
        {
            public double Lat { get; set; }
            public double Lon { get; set; }
            public string City { get; set; } = "";
            public string State { get; set; } = "";
        }

        private string ConvertStateToAbbreviation(string stateName)
        {
            // If already an abbreviation, return as-is
            if (stateName.Length == 2 && stateName.All(char.IsUpper))
                return stateName;

            // Common state name to abbreviation mapping
            var stateMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                {"Alabama", "AL"}, {"Alaska", "AK"}, {"Arizona", "AZ"}, {"Arkansas", "AR"},
                {"California", "CA"}, {"Colorado", "CO"}, {"Connecticut", "CT"}, {"Delaware", "DE"},
                {"Florida", "FL"}, {"Georgia", "GA"}, {"Hawaii", "HI"}, {"Idaho", "ID"},
                {"Illinois", "IL"}, {"Indiana", "IN"}, {"Iowa", "IA"}, {"Kansas", "KS"},
                {"Kentucky", "KY"}, {"Louisiana", "LA"}, {"Maine", "ME"}, {"Maryland", "MD"},
                {"Massachusetts", "MA"}, {"Michigan", "MI"}, {"Minnesota", "MN"}, {"Mississippi", "MS"},
                {"Missouri", "MO"}, {"Montana", "MT"}, {"Nebraska", "NE"}, {"Nevada", "NV"},
                {"New Hampshire", "NH"}, {"New Jersey", "NJ"}, {"New Mexico", "NM"}, {"New York", "NY"},
                {"North Carolina", "NC"}, {"North Dakota", "ND"}, {"Ohio", "OH"}, {"Oklahoma", "OK"},
                {"Oregon", "OR"}, {"Pennsylvania", "PA"}, {"Rhode Island", "RI"}, {"South Carolina", "SC"},
                {"South Dakota", "SD"}, {"Tennessee", "TN"}, {"Texas", "TX"}, {"Utah", "UT"},
                {"Vermont", "VT"}, {"Virginia", "VA"}, {"Washington", "WA"}, {"West Virginia", "WV"},
                {"Wisconsin", "WI"}, {"Wyoming", "WY"}
            };

            return stateMap.TryGetValue(stateName, out var abbr) ? abbr : stateName;
        }


        /// <summary>
        /// Get NWS forecast data for coordinates
        /// </summary>
        public async Task<(List<SnowDayForecast> forecasts, string city, string state)> GetSnowDayForecast(string zipCode)
        {
            var coords = await GetCoordinatesFromZip(zipCode);
            if (!coords.HasValue)
            {
                throw new Exception("Invalid ZIP code");
            }

            return await GetSnowDayForecastFromCoords(coords.Value.lat, coords.Value.lon);
        }

        /// <summary>
        /// Get snow day forecast from coordinates
        /// </summary>
        public async Task<(List<SnowDayForecast> forecasts, string city, string state)> GetSnowDayForecastFromCoords(
            double latitude,
            double longitude)
        {
            // Get climate data
            var climate = await GetClimateDataAsync(latitude, longitude);
            if (climate == null)
            {
                throw new Exception("Failed to fetch climate data");
            }

            // Get NWS points data
            var pointsUrl = $"https://api.weather.gov/points/{latitude:F4},{longitude:F4}";
            var pointsResponse = await _httpClient.GetFromJsonAsync<NWSPointsResponse>(pointsUrl);

            if (pointsResponse?.Properties == null)
            {
                throw new Exception("Failed to get NWS location data");
            }

            var city = pointsResponse.Properties.RelativeLocation?.Properties?.City ?? "Unknown";
            var state = pointsResponse.Properties.RelativeLocation?.Properties?.State ?? "";
            climate.State = state;

            // Get forecast
            var forecastUrl = pointsResponse.Properties.Forecast;
            var forecastResponse = await _httpClient.GetFromJsonAsync<NWSForecastResponse>(forecastUrl);

            if (forecastResponse?.Properties?.Periods == null)
            {
                throw new Exception("Failed to get forecast data");
            }

            // Get active alerts
            var alerts = await GetActiveAlertsAsync(latitude, longitude);

            // Calculate snow day probabilities
            var forecasts = CalculateSnowDayProbabilities(
                forecastResponse.Properties.Periods,
                climate,
                alerts
            );

            return (forecasts, city, state);
        }

        /// <summary>
        /// Calculate snow day probabilities using simplified climate-based algorithm
        /// </summary>
        private List<SnowDayForecast> CalculateSnowDayProbabilities(
            List<Period> periods,
            GeographyContext climate,
            List<WeatherAlert> alerts)
        {
            // Call with empty snow events dictionary (no historical data)
            return CalculateSnowDayProbabilitiesWithHistory(periods, climate, alerts, new Dictionary<DateTime, double>());
        }

        /// <summary>
        /// Calculate snow day probabilities with historical snow events
        /// </summary>
        private List<SnowDayForecast> CalculateSnowDayProbabilitiesWithHistory(
            List<Period> periods,
            GeographyContext climate,
            List<WeatherAlert> alerts,
            Dictionary<DateTime, double> historicalSnowEvents)
        {
            var forecasts = new List<SnowDayForecast>();
            var allDailyPeriods = periods.Where(p => p.IsDaytime).Take(7).ToList();

            Console.WriteLine($"Processing {allDailyPeriods.Count} forecast periods...");

            // Start with historical snow events (convert to new structure)
            var winterEvents = new Dictionary<DateTime, WinterEvent>();
            foreach (var evt in historicalSnowEvents)
            {
                winterEvents[evt.Key] = new WinterEvent
                {
                    EffectiveAmount = evt.Value,
                    IsIceEvent = false,  // Historical events are snow-based
                    OriginalIceAmount = 0
                };
            }

            // FIRST PASS: Extract snow AND ICE amounts from ALL days to build winter event history
            Console.WriteLine("\n=== FIRST PASS: Building winter event history ===");
            foreach (var period in allDailyPeriods)
            {
                var nightPeriod = periods.FirstOrDefault(p =>
                    p.StartTime.Date == period.StartTime.Date && !p.IsDaytime);

                double snowAmount = ExtractSnowAmount(period, nightPeriod);
                double iceAmount = ExtractIceAmount(period, nightPeriod);

                // Track significant winter events (snow OR ice)
                // Ice is 3x more impactful, so even small amounts matter
                if (snowAmount >= 1.0 || iceAmount >= 0.05)
                {
                    // Store as effective snow (ice × 3 for impact)
                    double effectiveAmount = snowAmount + (iceAmount * 3.0);
                    bool isIceEvent = iceAmount >= 0.05;  // Primarily ice if we detected ice

                    winterEvents[period.StartTime.Date] = new WinterEvent
                    {
                        EffectiveAmount = effectiveAmount,
                        IsIceEvent = isIceEvent,
                        OriginalIceAmount = iceAmount
                    };

                    if (isIceEvent)
                    {
                        Console.WriteLine($"{period.Name} ({period.StartTime.Date:MM/dd}): {iceAmount:F2}\" ice ({effectiveAmount:F2}\" effective) - TRACKED");
                    }
                    else
                    {
                        Console.WriteLine($"{period.Name} ({period.StartTime.Date:MM/dd}): {snowAmount:F1}\" snow - TRACKED");
                    }
                }
                else
                {
                    // NEW: Check for keyword-based snow events (no explicit amounts but snow in forecast)
                    // This ensures aftermath logic works for days following keyword-based forecasts
                    var keywordSnow = EstimateSnowFromKeywords(period, nightPeriod);
                    if (keywordSnow > 0)
                    {
                        winterEvents[period.StartTime.Date] = new WinterEvent
                        {
                            EffectiveAmount = keywordSnow,
                            IsIceEvent = false,
                            OriginalIceAmount = 0
                        };
                        Console.WriteLine($"{period.Name} ({period.StartTime.Date:MM/dd}): ~{keywordSnow:F1}\" snow (keyword-based) - TRACKED");
                    }
                }
            }

            // SECOND PASS: Calculate probabilities for each day
            Console.WriteLine("\n=== SECOND PASS: Calculating probabilities ===");
            foreach (var period in allDailyPeriods)
            {
                var nightPeriod = periods.FirstOrDefault(p =>
                    p.StartTime.Date == period.StartTime.Date && !p.IsDaytime);

                Console.WriteLine($"\n{period.Name} ({period.StartTime.Date:MM/dd}):");
                Console.WriteLine($"  Forecast: {period.ShortForecast}");
                Console.WriteLine($"  Temp: {period.Temperature}°F, Precip: {period.ProbabilityOfPrecipitation?.Value ?? 0}%");

                // Extract snow and ice amounts
                double snowAmount = ExtractSnowAmount(period, nightPeriod);
                double iceAmount = ExtractIceAmount(period, nightPeriod);

                Console.WriteLine($"  Extracted snow: {snowAmount:F2}\", ice: {iceAmount:F2}\"");

                // Calculate probabilities (even for weekends, so we can see the logic)
                var (closureChance, delayChance, isAftermath, daysSince) = CalculateDayProbabilities(
                    period.StartTime.Date,
                    snowAmount,
                    iceAmount,
                    period.Temperature,
                    period.ProbabilityOfPrecipitation?.Value ?? 0,
                    climate,
                    winterEvents,
                    alerts,
                    period,
                    nightPeriod
                );

                Console.WriteLine($"  Result: Closure={closureChance}%, Delay={delayChance}%{(isAftermath ? $" (aftermath, {daysSince}d since event)" : "")}");

                // Get snow description for display (range or keyword)
                var snowDescription = GetSnowDescription(period, nightPeriod);

                forecasts.Add(new SnowDayForecast
                {
                    DayName = period.Name,
                    Date = period.StartTime.Date,
                    SnowDayChance = closureChance,
                    DelayChance = delayChance,
                    Temperature = period.Temperature,
                    Forecast = period.ShortForecast,
                    PrecipitationChance = period.ProbabilityOfPrecipitation?.Value ?? 0,
                    SnowfallAmount = !string.IsNullOrEmpty(snowDescription) ? snowDescription : null,
                    IsAftermathDay = isAftermath,
                    DaysSinceSnowEvent = daysSince
                });
            }

            return forecasts;
        }

        /// <summary>
        /// Calculate closure and delay probabilities for a single day
        /// </summary>
        private (int closureChance, int delayChance, bool isAftermath, int daysSince) CalculateDayProbabilities(
            DateTime date,
            double snowAmount,
            double iceAmount,
            int temperature,
            int precipChance,
            GeographyContext climate,
            Dictionary<DateTime, WinterEvent> winterEvents,
            List<WeatherAlert> alerts,
            Period period,
            Period? nightPeriod)
        {
            // Check for aftermath from previous snow events FIRST
            var (aftermathClosure, aftermathDelay, days) = CalculateAftermathProbabilities(
                date,
                temperature,
                climate,
                winterEvents,
                alerts
            );

            // Check direct probability (handles both explicit amounts AND keywords)
            var (directClosure, directDelay) = CalculateDirectSnowDayProbabilities(
                snowAmount,
                iceAmount,
                temperature,
                precipChance,
                climate,
                alerts,
                period,
                nightPeriod
            );

            // Use whichever is higher (direct snow/keyword or aftermath)
            if (directClosure > aftermathClosure)
            {
                return (directClosure, directDelay, false, 0);
            }

            // Return aftermath if it exists
            if (aftermathClosure > 0 || aftermathDelay > 0)
            {
                return (aftermathClosure, aftermathDelay, true, days);
            }

            // No snow and no aftermath
            return (0, 0, false, 0);
        }

        /// <summary>
        /// Calculate probabilities for a day with active snowfall OR keyword-based forecast
        /// </summary>
        private (int closureChance, int delayChance) CalculateDirectSnowDayProbabilities(
            double snowAmount,
            double iceAmount,
            int temperature,
            int precipChance,
            GeographyContext climate,
            List<WeatherAlert> alerts,
            Period period,
            Period? nightPeriod)
        {
            // If we have explicit amounts, use standard calculation
            if (snowAmount > 0 || iceAmount > 0)
            {
                Console.WriteLine($"  Direct snow calc: snow={snowAmount}\", ice={iceAmount}\", temp={temperature}°F, precip={precipChance}%");

                // Base calculation using climate-adjusted threshold
                double threshold = climate.ClosureThresholdInches;
                Console.WriteLine($"  Closure threshold: {threshold:F2}\"");

                // Ice is more dangerous - amplify its effect
                double effectiveSnow = snowAmount + (iceAmount * 3.0);
                Console.WriteLine($"  Effective snow: {effectiveSnow:F2}\"");

                // Temperature factor: colder = more dangerous (black ice, harder to clear)
                double tempFactor = 1.0;
                if (temperature < 20) tempFactor = 1.3;
                else if (temperature < 25) tempFactor = 1.15;

                // Calculate raw probability
                double rawProbability = (effectiveSnow * tempFactor) / threshold;
                int baseClosureChance = (int)Math.Min(100, rawProbability * 100);
                Console.WriteLine($"  Raw probability: {rawProbability:F2} -> {baseClosureChance}%");

                // Apply precipitation chance modifier
                baseClosureChance = (int)(baseClosureChance * (precipChance / 100.0));
                Console.WriteLine($"  After precip modifier ({precipChance}%): {baseClosureChance}%");

                // Apply alert bonus
                int alertBonus = GetAlertBonus(alerts);
                int finalClosureChance = Math.Min(95, baseClosureChance + alertBonus);
                Console.WriteLine($"  Alert bonus: +{alertBonus}% -> Final: {finalClosureChance}%");

                // Delay logic
                int delayChance = CalculateDelayFromClosure(finalClosureChance);
                Console.WriteLine($"  Delay: {delayChance}%");

                return (finalClosureChance, Math.Min(95, delayChance));
            }

            // NEW: Keyword-based fallback for vague forecasts (no explicit amounts)
            var forecast = period.ShortForecast?.ToLower() ?? "";

            // Determine base keyword probability
            double baseKeyword = 0;

            if (forecast.Contains("heavy snow"))
                baseKeyword = 70;
            else if (forecast.Contains("light snow") && !forecast.Contains("chance"))
                baseKeyword = 25;
            else if (forecast.Contains("chance") && forecast.Contains("snow") && !forecast.Contains("slight"))
                baseKeyword = 15;
            else if (forecast.Contains("slight chance") && forecast.Contains("snow"))
                baseKeyword = 5;
            else if (forecast.Contains("snow showers"))
                baseKeyword = 10;

            if (baseKeyword == 0)
                return (0, 0);

            // Apply preparedness adjustment (same logic as snow calculations)
            double prepFactor = 1.0 + (climate.PreparednessIndex * 5.0);
            double adjustedKeyword = baseKeyword / prepFactor;

            // Apply precipitation probability
            adjustedKeyword *= (precipChance / 100.0);

            // Winter alert bonus
            int keywordAlertBonus = GetAlertBonus(alerts);
            if (keywordAlertBonus > 0)
                adjustedKeyword = Math.Min(adjustedKeyword + 20, 95);

            // Delays are 2.5x more likely than closures for vague forecasts
            double keywordDelay = Math.Min(adjustedKeyword * 2.5, 85);

            if (adjustedKeyword > 0)
                Console.WriteLine($"  Keyword probability: '{forecast}' (prep-adjusted) → Closure={adjustedKeyword:F0}%, Delay={keywordDelay:F0}%");

            return ((int)Math.Round(adjustedKeyword), (int)Math.Round(keywordDelay));
        }

        /// <summary>
        /// Calculate delay probability based on closure probability
        /// </summary>
        private int CalculateDelayFromClosure(int closureChance)
        {
            // High closure (80%+) → very low delay (10-20%)
            // Medium closure (40-80%) → moderate delay (30-50%)
            // Low closure (10-40%) → higher delay (40-70%)
            int delayChance = 0;

            if (closureChance >= 80)
                delayChance = Math.Max(5, 20 - (closureChance - 80));
            else if (closureChance >= 60)
                delayChance = 30;
            else if (closureChance >= 40)
                delayChance = 50;
            else if (closureChance >= 20)
                delayChance = Math.Min(75, 40 + closureChance);
            else if (closureChance >= 5)
                delayChance = Math.Min(60, closureChance * 3);

            return delayChance;
        }

        // Add this class inside WeatherService (before CalculateSnowDayProbabilitiesWithHistory)
        private class WinterEvent
        {
            public double EffectiveAmount { get; set; }  // Snow or ice×3
            public bool IsIceEvent { get; set; }          // True if primarily ice
            public double OriginalIceAmount { get; set; } // Original ice amount
        }

        /// <summary>
        /// Calculate aftermath probabilities with ice-aware logic
        /// Ice events: High Day 1, fast decay (melts quickly above 32°F)
        /// Snow events: Moderate Day 1, slow decay (persists longer)
        /// </summary>
        private (int closureChance, int delayChance, int daysSince) CalculateAftermathProbabilities(
            DateTime currentDate,
            int temperature,
            GeographyContext climate,
            Dictionary<DateTime, WinterEvent> winterEvents,
            List<WeatherAlert> alerts)
        {
            Console.WriteLine($"  Aftermath check for {currentDate:MM/dd}:");
            Console.WriteLine($"    Winter events in history: {winterEvents.Count}");
            foreach (var evt in winterEvents)
            {
                var type = evt.Value.IsIceEvent ? "ice" : "snow";
                Console.WriteLine($"      {evt.Key:MM/dd}: {evt.Value.EffectiveAmount:F1}\" ({type})");
            }

            int maxClosureChance = 0;
            int maxDelayChance = 0;
            int closestDays = 0;

            foreach (var winterEvent in winterEvents)
            {
                var daysSince = (currentDate - winterEvent.Key).Days;
                var evt = winterEvent.Value;

                Console.WriteLine($"    Checking event {winterEvent.Key:MM/dd} ({evt.EffectiveAmount:F1}\" {(evt.IsIceEvent ? "ice" : "snow")}): {daysSince} days ago");

                // Only consider events within typical closure period
                if (daysSince <= 0)
                {
                    Console.WriteLine($"      Skipped: daysSince <= 0");
                    continue;
                }

                // SPECIAL: Snow events Day 5-6 can have delay-only aftermath (no closures)
                if (!evt.IsIceEvent && daysSince >= 5 && daysSince <= 6)
                {
                    // Calculate delay-only for Day 5-6 (roads clear enough to open, but buses slower)
                    double day56PrepFactor = 1.5 - climate.PreparednessIndex;
                    double day56Threshold = climate.ClosureThresholdInches;
                    double day56RawRatio = evt.EffectiveAmount / day56Threshold;

                    // Only apply to major events (>2x threshold = significant storm)
                    if (day56RawRatio >= 2.0)
                    {
                        double day56BaseProb;
                        if (day56RawRatio >= 2.5) day56BaseProb = 98.0 * day56PrepFactor;
                        else if (day56RawRatio >= 2.0) day56BaseProb = 95.0 * day56PrepFactor;
                        else day56BaseProb = 70.0 * day56PrepFactor;

                        day56BaseProb = Math.Min(100, day56BaseProb);

                        // Day 5-6: Delays only (school open, buses slower)
                        double day56DelayProb;
                        if (daysSince == 5)
                            day56DelayProb = Math.Min(day56BaseProb * 0.30, 30); // 30% of base, max 30%
                        else // daysSince == 6
                            day56DelayProb = Math.Min(day56BaseProb * 0.15, 15); // 15% of base, max 15%

                        // Major storms (>6") extend longer
                        if (evt.EffectiveAmount > 6.0)
                            day56DelayProb = Math.Min(day56DelayProb * 1.2, 40);

                        int day56DelayChance = (int)Math.Round(day56DelayProb);

                        if (day56DelayChance > 0)
                        {
                            Console.WriteLine($"      SNOW Day {daysSince} delay-only: {day56DelayChance}%");

                            if (day56DelayChance > maxDelayChance)
                            {
                                maxClosureChance = 0; // No closures on Day 5-6
                                maxDelayChance = day56DelayChance;
                                closestDays = daysSince;
                                Console.WriteLine($"      *** NEW MAX (delay only) ***");
                            }
                        }
                    }

                    continue; // Skip normal aftermath logic for Day 5-6
                }

                if (daysSince > climate.TypicalClosureDays)
                {
                    Console.WriteLine($"      Skipped: beyond typical closure period ({climate.TypicalClosureDays} days)");
                    continue;
                }

                var effectiveAmount = evt.EffectiveAmount;

                // Base aftermath probability scaled by preparedness
                double prepFactor = 1.5 - climate.PreparednessIndex; // 0.0→1.5x, 1.0→0.5x
                Console.WriteLine($"      Prep factor: {prepFactor:F2}");

                // Calculate base probability relative to closure threshold
                double threshold = climate.ClosureThresholdInches;
                double rawRatio = effectiveAmount / threshold;
                Console.WriteLine($"      Ratio: {effectiveAmount:F1}\" / {threshold:F2}\" = {rawRatio:F2}x threshold");

                // Scale base probability by how much exceeded threshold
                double baseProb;
                if (rawRatio >= 2.5) baseProb = 98.0 * prepFactor;
                else if (rawRatio >= 2.0) baseProb = 95.0 * prepFactor;
                else if (rawRatio >= 1.5) baseProb = 85.0 * prepFactor;
                else if (rawRatio >= 1.0) baseProb = 70.0 * prepFactor;
                else if (rawRatio >= 0.75) baseProb = 50.0 * prepFactor;
                else baseProb = 30.0 * prepFactor;

                baseProb = Math.Min(100, baseProb);
                Console.WriteLine($"      Base prob: {baseProb:F1}%");

                // ========================================================================
                // ICE-AWARE DECAY LOGIC - Data-driven based on preparedness
                // ========================================================================
                double finalProb = baseProb;

                if (evt.IsIceEvent)
                {
                    // ICE EVENTS: Decay depends on temperature - ice only melts above 32°F
                    double iceDay1Boost = 1.5 + (1.0 - climate.PreparednessIndex);

                    if (daysSince == 1)
                    {
                        // Day 1 after ice is CRITICAL
                        finalProb = Math.Min(95, baseProb * iceDay1Boost);
                        Console.WriteLine($"      ICE Day 1 boost (×{iceDay1Boost:F2}): {finalProb:F1}%");
                    }
                    else if (temperature <= 32)
                    {
                        // Ice PERSISTS when frozen - slow decay like snow
                        double iceSlowDecay = 0.85;
                        finalProb = baseProb * iceDay1Boost * Math.Pow(iceSlowDecay, daysSince - 1);
                        Console.WriteLine($"      ICE Day {daysSince} frozen ({temperature}°F): {finalProb:F1}%");
                    }
                    else
                    {
                        // Ice melts FAST above 32°F
                        double iceFastDecay = 0.50;
                        finalProb = baseProb * Math.Pow(iceFastDecay, daysSince);
                        Console.WriteLine($"      ICE Day {daysSince} melting ({temperature}°F): {finalProb:F1}%");
                    }
                }
                else
                {
                    // SNOW EVENTS: Decay based on event size and preparedness
                    // Small events (< 1") decay fast - likely keyword-based estimates
                    bool isSmallEvent = effectiveAmount < 1.0;

                    if (isSmallEvent)
                    {
                        // Small events: Day 1 only, then near-zero
                        if (daysSince == 1)
                        {
                            finalProb = baseProb * 0.80;
                            Console.WriteLine($"      SNOW Day 1 (small event): {finalProb:F1}%");
                        }
                        else
                        {
                            finalProb = baseProb * 0.10; // Minimal by Day 2+
                            Console.WriteLine($"      SNOW Day {daysSince} (small event, fast decay): {finalProb:F1}%");
                        }
                    }
                    else if (daysSince == 1)
                    {
                        finalProb = baseProb * 0.95;
                        Console.WriteLine($"      SNOW Day 1 decay: {finalProb:F1}%");
                    }
                    else if (daysSince == 2)
                    {
                        finalProb = baseProb * 0.90;
                        Console.WriteLine($"      SNOW Day 2 decay: {finalProb:F1}%");
                    }
                    else if (daysSince == 3)
                    {
                        finalProb = baseProb * 0.70;
                        Console.WriteLine($"      SNOW Day 3 decay: {finalProb:F1}%");
                    }
                    else
                    {
                        double decayRate = climate.AftermathDecayRate;
                        finalProb = baseProb * Math.Pow(1.0 - decayRate, daysSince);
                        Console.WriteLine($"      SNOW Day {daysSince} decay: {finalProb:F1}%");
                    }
                }

                // Apply temperature melt factor (affects both ice and snow)
                double closureProb = finalProb;
                if (temperature > 32)
                {
                    double meltFactor = climate.GetMeltFactor(temperature);
                    double before = closureProb;

                    // Ice melts faster than snow, so melt factor is stronger for ice
                    double meltMultiplier = evt.IsIceEvent ? 0.7 : 0.5;
                    closureProb *= (1.0 - meltFactor * meltMultiplier);

                    Console.WriteLine($"      Temp {temperature}°F melt: {before:F1}% → {closureProb:F1}%");
                }

                int closureChance = (int)Math.Round(closureProb);

                // Calculate delay separately based on event type
                int delayChance;
                if (evt.IsIceEvent)
                {
                    // Ice events: Use inverse delay relationship (existing logic)
                    if (closureChance >= 85)
                        delayChance = Math.Max(10, 20 - (closureChance - 85) / 2);
                    else if (closureChance >= 70)
                        delayChance = 30;
                    else if (closureChance >= 50)
                        delayChance = Math.Min(65, closureChance + 15);
                    else if (closureChance >= 25)
                        delayChance = Math.Min(70, closureChance + 30);
                    else
                        delayChance = Math.Min(50, closureChance * 2);
                }
                else
                {
                    // SNOW events: Delays persist longer than closures
                    // Based on closureProb (which has preparedness and melt already applied)
                    double delayProb;
                    if (daysSince == 1)
                        delayProb = Math.Min(closureProb * 0.90, 85);
                    else if (daysSince == 2)
                        delayProb = Math.Min(closureProb * 0.85, 75);
                    else if (daysSince == 3)
                        delayProb = Math.Min(closureProb * 0.85, 65);
                    else if (daysSince == 4)
                        delayProb = Math.Min(closureProb * 3.0, 50);
                    else if (daysSince == 5)
                        delayProb = Math.Min(closureProb * 2.5, 30);
                    else
                        delayProb = Math.Min(closureProb * 2.0, 20);

                    // Major storms (>6") have longer delay impact
                    if (evt.EffectiveAmount > 6.0)
                        delayProb = Math.Min(delayProb * 1.2, 95);

                    delayChance = (int)Math.Round(delayProb);
                }

                Console.WriteLine($"      Final: Closure={closureChance}%, Delay={delayChance}%");

                if (closureChance > maxClosureChance)
                {
                    maxClosureChance = closureChance;
                    maxDelayChance = delayChance;
                    closestDays = daysSince;
                    Console.WriteLine($"      *** NEW MAX ***");
                }
            }

            // Apply alert bonus to aftermath ONLY if alerts are still active for this date
            int alertBonus = GetAlertBonusForDate(alerts, currentDate);
            if (alertBonus > 0 && (maxClosureChance > 0 || maxDelayChance > 0))
            {
                // Alerts boost aftermath - but only when alert covers this day
                int boostedClosure = Math.Min(95, maxClosureChance + alertBonus / 2);
                int boostedDelay = Math.Min(95, maxDelayChance + alertBonus / 2);
                Console.WriteLine($"    Alert bonus (+{alertBonus / 2}%): Closure={maxClosureChance}%→{boostedClosure}%, Delay={maxDelayChance}%→{boostedDelay}%");
                maxClosureChance = boostedClosure;
                maxDelayChance = boostedDelay;
            }
            else if (alertBonus > 0 && maxClosureChance == 0 && maxDelayChance == 0)
            {
                Console.WriteLine($"    Alert active for {currentDate:MM/dd} (+{alertBonus}% potential) but no aftermath events");
            }
            else if (GetAlertBonus(alerts) > 0 && alertBonus == 0)
            {
                Console.WriteLine($"    Alerts exist but expired before {currentDate:MM/dd}");
            }

            Console.WriteLine($"    Aftermath result: Closure={maxClosureChance}%, Delay={maxDelayChance}%");
            return (Math.Min(95, maxClosureChance), Math.Min(95, maxDelayChance), closestDays);
        }

        /// <summary>
        /// Estimate potential snow amount from keywords when no explicit amount is given
        /// Used in first pass to track potential winter events for aftermath calculations
        /// </summary>
        private double EstimateSnowFromKeywords(Period dayPeriod, Period? nightPeriod)
        {
            var forecast = (dayPeriod.ShortForecast ?? "").ToLower();
            var nightForecast = (nightPeriod?.ShortForecast ?? "").ToLower();
            var combinedForecast = forecast + " " + nightForecast;

            var precipChance = Math.Max(
                dayPeriod.ProbabilityOfPrecipitation?.Value ?? 0,
                nightPeriod?.ProbabilityOfPrecipitation?.Value ?? 0
            );
            var temp = Math.Min(
                dayPeriod.Temperature,
                nightPeriod?.Temperature ?? dayPeriod.Temperature
            );

            // Must be freezing and have some precip chance
            if (temp > 34 || precipChance < 20)
                return 0;

            // Estimate base snow amount from keywords
            double baseSnow = 0;

            if (combinedForecast.Contains("heavy snow") || combinedForecast.Contains("blizzard"))
                baseSnow = 6.0;
            else if (combinedForecast.Contains("snow") && !combinedForecast.Contains("slight") && !combinedForecast.Contains("chance"))
                baseSnow = 3.0;  // "Snow" or "Light Snow" without qualifiers
            else if (combinedForecast.Contains("chance") && combinedForecast.Contains("snow") && !combinedForecast.Contains("slight"))
                baseSnow = 2.0;  // "Chance Snow Showers", "Chance Snow"
            else if (combinedForecast.Contains("snow showers"))
                baseSnow = 1.5;
            else if (combinedForecast.Contains("slight chance") && combinedForecast.Contains("snow"))
                baseSnow = 1.0;
            else if (combinedForecast.Contains("flurries"))
                baseSnow = 0.5;

            if (baseSnow == 0)
                return 0;

            // Scale by precipitation probability
            // Higher precip chance = more likely to actually accumulate
            double scaledSnow = baseSnow * (precipChance / 100.0);

            // Only track if estimated amount is significant enough for aftermath
            // At least 0.5" effective to matter for road conditions
            return scaledSnow >= 0.5 ? scaledSnow : 0;
        }

        /// <summary>
        /// Extract snow amount from day and night periods - EXPLICIT AMOUNTS ONLY
        /// No keyword-based estimates (those are handled in direct probability calculation)
        /// </summary>
        private double ExtractSnowAmount(Period dayPeriod, Period? nightPeriod)
        {
            double total = 0;

            // Try explicit NWS snowfall values
            if (dayPeriod.SnowfallAmount?.Value.HasValue == true)
                total += dayPeriod.SnowfallAmount.Value.Value;
            if (nightPeriod?.SnowfallAmount?.Value.HasValue == true)
                total += nightPeriod.SnowfallAmount.Value.Value;

            if (total > 0)
            {
                Console.WriteLine($"  Explicit snow from NWS: {total:F1}\"");
                return total;
            }

            // Try parsing explicit amounts from text (e.g., "6-10 inches possible")
            var detailed = dayPeriod.DetailedForecast ?? "";
            var nightDetailed = nightPeriod?.DetailedForecast ?? "";

            total = ParseSnowFromText(detailed, dayPeriod.ShortForecast);
            if (nightPeriod != null)
                total += ParseSnowFromText(nightDetailed, nightPeriod.ShortForecast);

            if (total > 0)
                Console.WriteLine($"  Parsed from text: {total:F1}\"");

            return total; // Returns 0 if no explicit amount found (keywords handled separately)
        }

        /// <summary>
        /// Parse snow amounts from forecast text when NWS doesn't provide structured data
        /// Only parses EXPLICIT amounts like "6-10 inches" - NO keyword estimates
        /// </summary>
        private double ParseSnowFromText(string detailedForecast, string shortForecast)
        {
            var text = (detailedForecast + " " + shortForecast).ToLower();

            // Look for patterns like "4 to 8 inches", "6 inches", etc.
            var patterns = new[]
            {
                @"(\d+)\s*to\s*(\d+)\s*inch", // "4 to 8 inches"
                @"(\d+)\s*-\s*(\d+)\s*inch",   // "4-8 inches"
                @"around\s*(\d+)\s*inch",       // "around 6 inches"
                @"(\d+)\s*inch",                // "6 inches"
            };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
                if (match.Success)
                {
                    if (match.Groups.Count == 3) // Range like "4 to 8"
                    {
                        var low = double.Parse(match.Groups[1].Value);
                        var high = double.Parse(match.Groups[2].Value);
                        var avg = (low + high) / 2.0;
                        Console.WriteLine($"    Parsed from text: {low}-{high}\" (avg {avg:F1}\")");
                        return avg;
                    }
                    else if (match.Groups.Count == 2) // Single amount like "6"
                    {
                        var amount = double.Parse(match.Groups[1].Value);
                        Console.WriteLine($"    Parsed from text: {amount}\"");
                        return amount;
                    }
                }
            }

            // NO keyword estimates - return 0 if no explicit amount found
            // Keyword-based probabilities are handled separately in direct calculation
            return 0;
        }

        /// <summary>
        /// Get snow range for display (e.g., "7-11 inches")
        /// </summary>
        private (double min, double max)? GetSnowRange(Period dayPeriod, Period? nightPeriod)
        {
            var combined = (dayPeriod.DetailedForecast ?? "") + " " + (nightPeriod?.DetailedForecast ?? "");

            // Match patterns like "6-10 inches" or "6 to 10 inches"
            var rangeMatch = System.Text.RegularExpressions.Regex.Match(combined, @"(\d+)\s*(?:to|-)\s*(\d+)\s*inch", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (rangeMatch.Success)
                return (double.Parse(rangeMatch.Groups[1].Value), double.Parse(rangeMatch.Groups[2].Value));

            // Match single amount like "8 inches"
            var singleMatch = System.Text.RegularExpressions.Regex.Match(combined, @"(\d+)\s*inch", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (singleMatch.Success)
            {
                double val = double.Parse(singleMatch.Groups[1].Value);
                return (val, val);
            }

            return null;
        }

        /// <summary>
        /// Get display-friendly snow description for UI
        /// </summary>
        private string GetSnowDescription(Period dayPeriod, Period? nightPeriod)
        {
            // Show range if available (e.g., "7-11\" snow")
            var range = GetSnowRange(dayPeriod, nightPeriod);
            if (range.HasValue)
            {
                return range.Value.min == range.Value.max
                    ? $"{range.Value.min:F0}\" snow"
                    : $"{range.Value.min:F0}-{range.Value.max:F0}\" snow";
            }

            // Otherwise show forecast term
            var forecast = (dayPeriod.ShortForecast ?? "").ToLower();

            if (forecast.Contains("heavy snow")) return "Heavy snow";
            if (forecast.Contains("light snow")) return "Light snow";
            if (forecast.Contains("snow showers")) return "Snow showers";
            if (forecast.Contains("snow")) return "Snow";

            return "";
        }

        /// <summary>
        /// Extract ice accumulation from day and night periods with conservative estimation
        /// Only estimates when forecast EXPLICITLY mentions freezing rain/ice
        /// </summary>
        private double ExtractIceAmount(Period dayPeriod, Period? nightPeriod)
        {
            double total = 0;

            // First, try to get explicit ice accumulation values from NWS
            if (dayPeriod.IceAccumulation?.Value.HasValue == true)
                total += dayPeriod.IceAccumulation.Value.Value;

            if (nightPeriod?.IceAccumulation?.Value.HasValue == true)
                total += nightPeriod.IceAccumulation.Value.Value;

            // If we got explicit values, return them
            if (total > 0)
            {
                Console.WriteLine($"  Explicit ice accumulation from NWS: {total:F2}\"");
                return total;
            }

            // NWS didn't provide ice values - but ONLY estimate if forecast explicitly mentions ice
            // This prevents phantom ice creation

            double dayIce = EstimateIceFromForecast(dayPeriod);
            double nightIce = nightPeriod != null ? EstimateIceFromForecast(nightPeriod) : 0;

            total = dayIce + nightIce;

            if (total > 0)
            {
                Console.WriteLine($"  Estimated ice from forecast text: {total:F2}\" (day: {dayIce:F2}\", night: {nightIce:F2}\")");
            }

            return total;
        }

        /// <summary>
        /// Estimate ice accumulation ONLY when forecast explicitly mentions freezing rain/ice
        /// Conservative approach - no phantom ice!
        /// </summary>
        private double EstimateIceFromForecast(Period period)
        {
            Console.WriteLine($"  DEBUG: EstimateIceFromForecast called for '{period.ShortForecast}'");
            var forecast = period.ShortForecast?.ToLower() ?? "";
            var detailed = period.DetailedForecast?.ToLower() ?? "";
            var precipChance = period.ProbabilityOfPrecipitation?.Value ?? 0;
            var temp = period.Temperature;

            // RULE 1: Must be at or below freezing
            if (temp >= 38)
                return 0;

            // RULE 2: Check for sleet FIRST (ice pellets) - bypass "no accumulation" check
            // Sleet creates icy roads even without traditional ice accumulation
            bool hasSleet = forecast.Contains("sleet") || forecast.Contains("ice pellets");
            if (hasSleet)
            {
                double sleetIce = 0.08 * (precipChance / 100.0);
                Console.WriteLine($"  DEBUG: Sleet detected - estimating {sleetIce:F2}\" ice equivalent");
                return sleetIce;
            }

            // RULE 3: Check if NWS explicitly says NO ice accumulation
            if (detailed.Contains("little or no ice accumulation") ||
                detailed.Contains("no ice accumulation expected"))
            {
                Console.WriteLine($"  DEBUG: NWS says no ice accumulation - returning 0");
                return 0;
            }

            // RULE 4: Must explicitly mention ICE ACCUMULATION terms in PRIMARY forecast
            bool hasFreezingRain = forecast.Contains("freezing rain");
            bool hasIceStorm = forecast.Contains("ice storm");
            bool hasGlaze = forecast.Contains("glaze") || forecast.Contains("glazing");
            bool hasFreezingDrizzle = forecast.Contains("freezing drizzle");

            // If none of these specific ice terms are in the SHORT forecast, return 0
            if (!hasFreezingRain && !hasIceStorm && !hasGlaze && !hasFreezingDrizzle)
            {
                return 0; // No phantom ice!
            }

            // OK, forecast explicitly mentions ice - estimate conservatively
            double baseIce = 0;

            if (hasIceStorm)
            {
                baseIce = 0.25; // Quarter inch
            }
            else if (detailed.Contains("heavy freezing rain"))
            {
                baseIce = 0.15; // Heavy freezing rain
            }
            else if (hasFreezingRain)
            {
                baseIce = 0.10; // One tenth inch (very dangerous in South)
            }
            else if (hasFreezingDrizzle)
            {
                baseIce = 0.05; // Light glaze
            }
            else if (hasGlaze)
            {
                baseIce = 0.03; // Minimal glaze
            }

            // Adjust by precipitation probability
            double adjustedIce = baseIce * (precipChance / 100.0);

            if (adjustedIce > 0)
            {
                Console.WriteLine($"  Ice estimation for '{forecast}': temp={temp}°F, base={baseIce:F2}\", precip={precipChance}%, result={adjustedIce:F2}\"");
            }

            return adjustedIce;
        }

        /// <summary>
        /// Get active weather alerts - NO CACHING, always fresh API call
        /// </summary>
        public async Task<List<WeatherAlert>> GetActiveAlertsAsync(double latitude, double longitude)
        {
            try
            {
                // LOG: Prove we're making a fresh API call
                Console.WriteLine($"🚨 FETCHING FRESH ALERTS from NWS for {latitude:F4},{longitude:F4} at {DateTime.Now:HH:mm:ss}");

                var url = $"https://api.weather.gov/alerts/active?point={latitude:F4},{longitude:F4}";
                var response = await _httpClient.GetFromJsonAsync<NWSAlertsResponse>(url);

                if (response?.Features == null)
                {
                    Console.WriteLine($"✅ No active alerts found");
                    return new List<WeatherAlert>();
                }

                var winterKeywords = new[] { "winter", "snow", "ice", "blizzard", "freezing" };

                var alerts = response.Features
                    .Where(f => winterKeywords.Any(k =>
                        f.Properties.Event.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    .Select(f => new WeatherAlert
                    {
                        Type = f.Properties.Event,
                        Headline = f.Properties.Headline,
                        Description = f.Properties.Description,
                        Severity = ParseAlertSeverity(f.Properties.Event),
                        Onset = f.Properties.Onset,
                        Expires = f.Properties.Expires,
                        Ends = f.Properties.Ends
                    })
                    .ToList();

                Console.WriteLine($"✅ FRESH ALERTS RECEIVED: {alerts.Count} winter-related alerts");
                return alerts;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Alert fetch failed: {ex.Message}");
                return new List<WeatherAlert>();
            }
        }

        private AlertSeverity ParseAlertSeverity(string eventType)
        {
            var lower = eventType.ToLower();
            if (lower.Contains("blizzard") || lower.Contains("ice storm")) return AlertSeverity.Extreme;
            if (lower.Contains("warning")) return AlertSeverity.Warning;
            if (lower.Contains("watch")) return AlertSeverity.Watch;
            if (lower.Contains("advisory")) return AlertSeverity.Advisory;
            return AlertSeverity.None;
        }

        private int GetAlertBonus(List<WeatherAlert> alerts)
        {
            if (!alerts.Any()) return 0;

            var maxSeverity = alerts.Max(a => a.Severity);
            return maxSeverity switch
            {
                AlertSeverity.Extreme => 60,   // Blizzard/Ice Storm
                AlertSeverity.Warning => 40,   // Winter Storm Warning
                AlertSeverity.Watch => 25,     // Winter Storm Watch
                AlertSeverity.Advisory => 15,  // Winter Weather Advisory
                _ => 0
            };
        }

        /// <summary>
        /// Get alert bonus only if alerts are active for a specific date
        /// Alerts have Onset/Expires/Ends - only apply bonus if date falls within alert period
        /// </summary>
        private int GetAlertBonusForDate(List<WeatherAlert> alerts, DateTime date)
        {
            if (!alerts.Any()) return 0;

            // Filter to alerts that are active for this date
            var activeAlerts = alerts.Where(a =>
            {
                // Use Ends if available, otherwise Expires
                var endDate = a.Ends ?? a.Expires;

                // If no end date, assume alert is for today/tomorrow only
                if (!endDate.HasValue)
                    return date.Date <= DateTime.Today.AddDays(1);

                // Alert is active if date is before the end date
                // (Onset is typically in the past or today)
                return date.Date <= endDate.Value.Date;
            }).ToList();

            if (!activeAlerts.Any())
            {
                Console.WriteLine($"    No alerts active for {date:MM/dd} (alerts expired)");
                return 0;
            }

            var maxSeverity = activeAlerts.Max(a => a.Severity);
            return maxSeverity switch
            {
                AlertSeverity.Extreme => 60,
                AlertSeverity.Warning => 40,
                AlertSeverity.Watch => 25,
                AlertSeverity.Advisory => 15,
                _ => 0
            };
        }


        // Add this helper class at the end of the WeatherService class (before the closing brace):
        private class ZipLocationData
        {
            public string City { get; set; } = "";
            public string State { get; set; } = "";
        }

        private string GetStateFromZipPrefix(string zipCode)
        {
            if (zipCode.Length < 3) return "";

            var prefix = int.TryParse(zipCode.Substring(0, 3), out var p) ? p : -1;

            if (prefix >= 100 && prefix <= 149) return "NY";
            if (prefix >= 150 && prefix <= 196) return "PA";
            if (prefix >= 197 && prefix <= 199) return "DE";
            if (prefix >= 200 && prefix <= 205) return "DC";
            if (prefix >= 206 && prefix <= 219) return "MD";
            if (prefix >= 220 && prefix <= 246) return "VA";
            if (prefix >= 247 && prefix <= 268) return "WV";
            if (prefix >= 270 && prefix <= 289) return "NC";
            if (prefix >= 290 && prefix <= 299) return "SC";
            if (prefix >= 300 && prefix <= 319) return "GA";
            if (prefix >= 320 && prefix <= 349) return "FL";
            if (prefix >= 350 && prefix <= 369) return "AL";
            if (prefix >= 370 && prefix <= 385) return "TN";
            if (prefix >= 386 && prefix <= 397) return "MS";
            if (prefix >= 398 && prefix <= 399) return "GA";
            if (prefix >= 400 && prefix <= 427) return "KY";
            if (prefix >= 430 && prefix <= 458) return "OH";
            if (prefix >= 460 && prefix <= 479) return "IN";
            if (prefix >= 480 && prefix <= 499) return "MI";
            if (prefix >= 500 && prefix <= 528) return "IA";
            if (prefix >= 530 && prefix <= 549) return "WI";
            if (prefix >= 550 && prefix <= 567) return "MN";
            if (prefix >= 570 && prefix <= 577) return "SD";
            if (prefix >= 580 && prefix <= 588) return "ND";
            if (prefix >= 590 && prefix <= 599) return "MT";
            if (prefix >= 600 && prefix <= 620) return "IL";
            if (prefix >= 622 && prefix <= 629) return "IL";
            if (prefix >= 630 && prefix <= 658) return "MO";
            if (prefix >= 660 && prefix <= 679) return "KS";
            if (prefix >= 680 && prefix <= 693) return "NE";
            if (prefix >= 700 && prefix <= 729) return "LA";
            if (prefix >= 730 && prefix <= 749) return "AR";
            if (prefix >= 750 && prefix <= 799) return "TX";
            if (prefix >= 800 && prefix <= 816) return "CO";
            if (prefix >= 820 && prefix <= 831) return "WY";
            if (prefix >= 832 && prefix <= 838) return "ID";
            if (prefix >= 840 && prefix <= 847) return "UT";
            if (prefix >= 850 && prefix <= 865) return "AZ";
            if (prefix >= 870 && prefix <= 884) return "NM";
            if (prefix >= 889 && prefix <= 898) return "NV";
            if (prefix >= 900 && prefix <= 961) return "CA";
            if (prefix >= 970 && prefix <= 979) return "OR";
            if (prefix >= 980 && prefix <= 994) return "WA";
            if (prefix >= 995 && prefix <= 999) return "AK";

            return "";
        }

        /// <summary>
        /// Get city name from coordinates (reverse geocoding)
        /// </summary>
        public async Task<(string cityName, string state)> GetCityNameFromCoordinates(double latitude, double longitude)
        {
            try
            {
                // Use NWS points endpoint which includes location info
                var pointsUrl = $"https://api.weather.gov/points/{latitude:F4},{longitude:F4}";
                var pointsResponse = await _httpClient.GetFromJsonAsync<NWSPointsResponse>(pointsUrl);

                if (pointsResponse?.Properties?.RelativeLocation?.Properties != null)
                {
                    var city = pointsResponse.Properties.RelativeLocation.Properties.City;
                    var state = pointsResponse.Properties.RelativeLocation.Properties.State;
                    return (city, state);
                }
            }
            catch { }

            return ($"Location ({latitude:F2}, {longitude:F2})", "");
        }

        /// <summary>
        /// Get weather forecast from NWS - NO CACHING, always fresh API call
        /// </summary>
        public async Task<(List<Period>? periods, string city, string state)> GetWeatherForecast(double latitude, double longitude)
        {
            try
            {
                // LOG: Prove we're making a fresh API call
                Console.WriteLine($"🔄 FETCHING FRESH FORECAST from NWS for {latitude:F4},{longitude:F4} at {DateTime.Now:HH:mm:ss}");

                // Get NWS points data
                var pointsUrl = $"https://api.weather.gov/points/{latitude:F4},{longitude:F4}";
                var pointsResponse = await _httpClient.GetFromJsonAsync<NWSPointsResponse>(pointsUrl);

                if (pointsResponse?.Properties == null)
                {
                    return (null, "", "");
                }

                var city = pointsResponse.Properties.RelativeLocation?.Properties?.City ?? "Unknown";
                var state = pointsResponse.Properties.RelativeLocation?.Properties?.State ?? "";

                // Get forecast
                var forecastUrl = pointsResponse.Properties.Forecast;
                Console.WriteLine($"🔄 Fetching forecast data from: {forecastUrl}");
                var forecastResponse = await _httpClient.GetFromJsonAsync<NWSForecastResponse>(forecastUrl);

                if (forecastResponse?.Properties?.Periods == null)
                {
                    return (null, city, state);
                }

                Console.WriteLine($"✅ FRESH FORECAST RECEIVED: {forecastResponse.Properties.Periods.Count} periods");
                return (forecastResponse.Properties.Periods, city, state);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Weather forecast fetch failed: {ex.Message}");
                return (null, "", "");
            }
        }


        /// <summary>
        /// Calculate snow day chances with automatic climate data fetching
        /// </summary>
        public async Task<List<SnowDayForecast>> CalculateSnowDayChancesAsync(
            List<Period> periods,
            GeographyContext geography,
            List<HistoricalWeatherDay> historicalWeather,
            List<WeatherAlert> alerts)
        {
            // Fetch climate data if not already set
            if (geography.AvgAnnualSnowfall == 0 && geography.Latitude != 0)
            {
                Console.WriteLine($"Fetching climate data for ({geography.Latitude}, {geography.Longitude})...");
                var climateData = await GetClimateDataAsync(geography.Latitude, geography.Longitude);
                if (climateData != null)
                {
                    geography.AvgAnnualSnowfall = climateData.AvgAnnualSnowfall;
                    geography.Longitude = climateData.Longitude; // Fix missing longitude
                    Console.WriteLine($"Climate data fetched: {geography.AvgAnnualSnowfall}\" avg annual snowfall");
                }
                else
                {
                    // Fallback
                    geography.AvgAnnualSnowfall = EstimateSnowfallByLatitude(geography.Latitude);
                    Console.WriteLine($"Using fallback estimate: {geography.AvgAnnualSnowfall}\"");
                }
            }

            // Build snow events dictionary from historical data
            var snowEvents = new Dictionary<DateTime, double>();
            foreach (var day in historicalWeather)
            {
                if (day.SnowfallInches >= 1.0)
                {
                    snowEvents[day.Date] = day.SnowfallInches;
                    Console.WriteLine($"Adding historical snow event: {day.Date:yyyy-MM-dd} - {day.SnowfallInches:F1}\"");
                }
            }

            return CalculateSnowDayProbabilitiesWithHistory(periods, geography, alerts, snowEvents);
        }

        /// <summary>
        /// Get recent weather data with localStorage caching (3-hour cache - historical data doesn't change frequently)
        /// </summary>
        public async Task<List<HistoricalWeatherDay>> GetRecentWeather(
            double latitude,
            double longitude,
            int daysBack)
        {
            var cacheKey = $"{latitude:F2},{longitude:F2}_{daysBack}";

            // Try to get from localStorage cache first (if JSRuntime is available)
            if (_jsRuntime != null)
            {
                try
                {
                    var cachedJson = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", $"{HistoricalWeatherCacheKey}_{cacheKey}");
                    if (!string.IsNullOrEmpty(cachedJson))
                    {
                        var cached = System.Text.Json.JsonSerializer.Deserialize<CachedHistoricalWeather>(cachedJson);
                        if (cached != null)
                        {
                            // Check if cache is still valid (3 hours)
                            var cacheAge = DateTime.Now - cached.CachedAt;
                            if (cacheAge.TotalHours < 3)
                            {
                                Console.WriteLine($"Using cached historical weather for {cacheKey} (age: {cacheAge.TotalMinutes:F0} min)");
                                return cached.Data;
                            }
                            else
                            {
                                Console.WriteLine($"Historical weather cache expired for {cacheKey} (age: {cacheAge.TotalHours:F1} hours)");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading historical weather cache: {ex.Message}");
                }
            }

            // Not in cache or cache expired - fetch from API
            try
            {
                var endDate = DateTime.Today.AddDays(-1); // Yesterday
                var startDate = endDate.AddDays(-daysBack + 1);

                var url = $"https://archive-api.open-meteo.com/v1/archive?" +
                          $"latitude={latitude:F4}&longitude={longitude:F4}" +
                          $"&start_date={startDate:yyyy-MM-dd}" +
                          $"&end_date={endDate:yyyy-MM-dd}" +
                          $"&daily=snowfall_sum,temperature_2m_max,temperature_2m_min,precipitation_sum" +
                          $"&temperature_unit=fahrenheit" +
                          $"&precipitation_unit=inch" +
                          $"&timezone=auto";

                Console.WriteLine($"Fetching historical weather from: {url}");

                var response = await _httpClient.GetFromJsonAsync<OpenMeteoHistoricalResponse>(url);

                if (response?.Daily == null || response.Daily.Time.Count == 0)
                {
                    Console.WriteLine("No historical weather data available");
                    return new();
                }

                var historicalDays = new List<HistoricalWeatherDay>();

                for (int i = 0; i < response.Daily.Time.Count; i++)
                {
                    var day = new HistoricalWeatherDay
                    {
                        Date = DateTime.Parse(response.Daily.Time[i]),
                        SnowfallInches = response.Daily.SnowfallSum[i],
                        TempMax = response.Daily.TempMax[i],
                        TempMin = response.Daily.TempMin[i],
                        Precipitation = response.Daily.PrecipitationSum[i]
                    };

                    if (day.SnowfallInches > 0.5)
                    {
                        Console.WriteLine($"Historical snow found: {day.Date:yyyy-MM-dd} - {day.SnowfallInches:F1}\" (Temp: {day.TempMax:F0}°F)");
                    }

                    historicalDays.Add(day);
                }

                // Cache the result in localStorage (3-hour cache)
                if (_jsRuntime != null && historicalDays.Any())
                {
                    try
                    {
                        var cacheData = new CachedHistoricalWeather
                        {
                            Data = historicalDays,
                            CachedAt = DateTime.Now
                        };
                        var json = System.Text.Json.JsonSerializer.Serialize(cacheData);
                        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", $"{HistoricalWeatherCacheKey}_{cacheKey}", json);
                        Console.WriteLine($"Cached historical weather data for {cacheKey}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error caching historical weather: {ex.Message}");
                    }
                }

                return historicalDays;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Historical weather fetch failed: {ex.Message}");
                return new();
            }
        }

        // Add this helper class at the end of the WeatherService class (before the closing brace):
        private class CachedHistoricalWeather
        {
            public List<HistoricalWeatherDay> Data { get; set; } = new();
            public DateTime CachedAt { get; set; }
        }


        /// <summary>
        /// Get active alerts (public wrapper)
        /// </summary>
        public Task<List<WeatherAlert>> GetActiveAlerts(double latitude, double longitude)
        {
            return GetActiveAlertsAsync(latitude, longitude);
        }
    }
}
