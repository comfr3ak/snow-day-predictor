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

        public WeatherService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SnowDayPredictor/1.0");
            _jsRuntime = null;
        }

        public WeatherService(HttpClient httpClient, IJSRuntime jsRuntime)
        {
            _httpClient = httpClient;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SnowDayPredictor/1.0");
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
        /// Get coordinates from ZIP code using geocoding API
        /// </summary>
        public async Task<(double lat, double lon)?> GetCoordinatesFromZip(string zipCode)
        {
            try
            {
                var url = $"https://api.zippopotam.us/us/{zipCode}";
                var response = await _httpClient.GetFromJsonAsync<JsonElement>(url);

                if (response.TryGetProperty("places", out var places) && places.GetArrayLength() > 0)
                {
                    var place = places[0];
                    var lat = double.Parse(place.GetProperty("latitude").GetString() ?? "0");
                    var lon = double.Parse(place.GetProperty("longitude").GetString() ?? "0");
                    return (lat, lon);
                }
            }
            catch { }

            return null;
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

            // Start with historical snow events
            var snowEvents = new Dictionary<DateTime, double>(historicalSnowEvents);

            // FIRST PASS: Extract snow amounts from ALL days (including weekends) to build snow events
            Console.WriteLine("\n=== FIRST PASS: Building snow event history ===");
            foreach (var period in allDailyPeriods)
            {
                var nightPeriod = periods.FirstOrDefault(p =>
                    p.StartTime.Date == period.StartTime.Date && !p.IsDaytime);

                double snowAmount = ExtractSnowAmount(period, nightPeriod);

                if (snowAmount >= 1.0)
                {
                    snowEvents[period.StartTime.Date] = snowAmount;
                    Console.WriteLine($"{period.Name} ({period.StartTime.Date:MM/dd}): {snowAmount:F1}\" snow - TRACKED");
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
                    snowEvents,
                    alerts
                );

                Console.WriteLine($"  Result: Closure={closureChance}%, Delay={delayChance}%{(isAftermath ? $" (aftermath, {daysSince}d since event)" : "")}");

                forecasts.Add(new SnowDayForecast
                {
                    DayName = period.Name,
                    Date = period.StartTime.Date,
                    SnowDayChance = closureChance,
                    DelayChance = delayChance,
                    Temperature = period.Temperature,
                    Forecast = period.ShortForecast,
                    PrecipitationChance = period.ProbabilityOfPrecipitation?.Value ?? 0,
                    SnowfallAmount = snowAmount > 0 ? $"{snowAmount:F1}\"" : null,
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
            Dictionary<DateTime, double> snowEvents,
            List<WeatherAlert> alerts)
        {
            // Check for aftermath from previous snow events FIRST
            var (aftermathClosure, aftermathDelay, days) = CalculateAftermathProbabilities(
                date,
                temperature,
                climate,
                snowEvents
            );

            // Check if this is a direct snow day (significant accumulation)
            // But only use direct calculation if it would be HIGHER than aftermath
            if (snowAmount >= 1.0 || iceAmount >= 0.1)
            {
                var (directClosure, directDelay) = CalculateDirectSnowDayProbabilities(
                    snowAmount,
                    iceAmount,
                    temperature,
                    precipChance,
                    climate,
                    alerts
                );

                // Use whichever is higher (major snow day or aftermath)
                if (directClosure > aftermathClosure)
                {
                    return (directClosure, directDelay, false, 0);
                }
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
        /// Calculate probabilities for a day with active snowfall
        /// </summary>
        private (int closureChance, int delayChance) CalculateDirectSnowDayProbabilities(
            double snowAmount,
            double iceAmount,
            int temperature,
            int precipChance,
            GeographyContext climate,
            List<WeatherAlert> alerts)
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

            // Delay logic: inverse relationship with closure
            // High closure (80%+) → very low delay (10-20%)
            // Medium closure (40-80%) → moderate delay (30-50%)
            // Low closure (10-40%) → higher delay (40-70%)
            int delayChance = 0;

            if (finalClosureChance >= 80)
            {
                // Nearly certain closure → minimal delay chance
                delayChance = Math.Max(5, 20 - (finalClosureChance - 80));
            }
            else if (finalClosureChance >= 60)
            {
                // Likely closure → low delay chance
                delayChance = 30;
            }
            else if (finalClosureChance >= 40)
            {
                // Uncertain → moderate delay
                delayChance = 50;
            }
            else if (finalClosureChance >= 20)
            {
                // Unlikely closure → higher delay
                delayChance = Math.Min(75, 40 + finalClosureChance);
            }
            else if (finalClosureChance >= 5)
            {
                // Very unlikely closure → delay more likely
                delayChance = Math.Min(60, finalClosureChance * 3);
            }

            Console.WriteLine($"  Delay: {delayChance}%");

            return (finalClosureChance, Math.Min(95, delayChance));
        }

        /// <summary>
        /// Calculate aftermath probabilities from previous snow events
        /// </summary>
        private (int closureChance, int delayChance, int daysSince) CalculateAftermathProbabilities(
            DateTime currentDate,
            int temperature,
            GeographyContext climate,
            Dictionary<DateTime, double> snowEvents)
        {
            Console.WriteLine($"  Aftermath check for {currentDate:MM/dd}:");
            Console.WriteLine($"    Snow events in history: {snowEvents.Count}");
            foreach (var evt in snowEvents)
            {
                Console.WriteLine($"      {evt.Key:MM/dd}: {evt.Value:F1}\"");
            }

            int maxClosureChance = 0;
            int maxDelayChance = 0;
            int closestDays = 0;

            foreach (var snowEvent in snowEvents)
            {
                var daysSince = (currentDate - snowEvent.Key).Days;

                Console.WriteLine($"    Checking event {snowEvent.Key:MM/dd} ({snowEvent.Value:F1}\"): {daysSince} days ago");

                // Only consider events within typical closure period
                if (daysSince <= 0)
                {
                    Console.WriteLine($"      Skipped: daysSince <= 0");
                    continue;
                }

                if (daysSince > climate.TypicalClosureDays)
                {
                    Console.WriteLine($"      Skipped: beyond typical closure period ({climate.TypicalClosureDays} days)");
                    continue;
                }

                var snowAmount = snowEvent.Value;

                // Base aftermath probability scaled by preparedness
                double prepFactor = 1.5 - climate.PreparednessIndex; // 0.0→1.5x, 1.0→0.5x
                Console.WriteLine($"      Prep factor: {prepFactor:F2}");

                // Calculate base probability relative to closure threshold (matches direct calculation logic)
                double threshold = climate.ClosureThresholdInches;
                double rawRatio = snowAmount / threshold;
                Console.WriteLine($"      Snow ratio: {snowAmount:F1}\" / {threshold:F2}\" = {rawRatio:F2}x threshold");

                // Scale base probability by how much snow exceeded threshold
                double baseProb;
                if (rawRatio >= 2.5) baseProb = 98.0 * prepFactor;      // Massive storm (2.5x+ threshold)
                else if (rawRatio >= 2.0) baseProb = 95.0 * prepFactor; // Major storm (2x+ threshold)
                else if (rawRatio >= 1.5) baseProb = 85.0 * prepFactor; // Significant storm (1.5x+ threshold)
                else if (rawRatio >= 1.0) baseProb = 70.0 * prepFactor; // At threshold - definitely closes
                else if (rawRatio >= 0.75) baseProb = 50.0 * prepFactor; // Close to threshold
                else baseProb = 30.0 * prepFactor;                        // Below threshold but still tracked

                baseProb = Math.Min(100, baseProb);
                Console.WriteLine($"      Base prob: {baseProb:F1}%");

                // Decay logic - keep very high for first few days
                double finalProb = baseProb;

                if (daysSince == 1)
                {
                    finalProb = baseProb * 0.95;
                    Console.WriteLine($"      Day 1 decay: {finalProb:F1}%");
                }
                else if (daysSince == 2)
                {
                    finalProb = baseProb * 0.90;
                    Console.WriteLine($"      Day 2 decay: {finalProb:F1}%");
                }
                else if (daysSince == 3)
                {
                    finalProb = baseProb * 0.70;
                    Console.WriteLine($"      Day 3 decay: {finalProb:F1}%");
                }
                else
                {
                    double decayRate = climate.AftermathDecayRate;
                    finalProb = baseProb * Math.Pow(1.0 - decayRate, daysSince);
                    Console.WriteLine($"      Day {daysSince} decay: {finalProb:F1}%");
                }

                // Apply temperature melt factor only if above freezing
                if (temperature > 32)
                {
                    double meltFactor = climate.GetMeltFactor(temperature);
                    double before = finalProb;
                    finalProb *= (1.0 - meltFactor * 0.5);
                    Console.WriteLine($"      Temp {temperature}°F melt: {before:F1}% → {finalProb:F1}%");
                }

                int closureChance = (int)Math.Round(finalProb);

                // Inverse delay relationship
                int delayChance;
                if (closureChance >= 85)
                {
                    delayChance = Math.Max(10, 20 - (closureChance - 85) / 2);
                }
                else if (closureChance >= 70)
                {
                    delayChance = 30;
                }
                else if (closureChance >= 50)
                {
                    delayChance = Math.Min(65, closureChance + 15);
                }
                else if (closureChance >= 25)
                {
                    delayChance = Math.Min(70, closureChance + 30);
                }
                else
                {
                    delayChance = Math.Min(50, closureChance * 2);
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

            Console.WriteLine($"    Aftermath result: Closure={maxClosureChance}%, Delay={maxDelayChance}%");
            return (Math.Min(95, maxClosureChance), Math.Min(95, maxDelayChance), closestDays);
        }

        /// <summary>
        /// Extract snow amount from day and night periods
        /// </summary>
        private double ExtractSnowAmount(Period dayPeriod, Period? nightPeriod)
        {
            double total = 0;

            if (dayPeriod.SnowfallAmount?.Value.HasValue == true)
            {
                total += dayPeriod.SnowfallAmount.Value.Value;
                Console.WriteLine($"    Day period has {dayPeriod.SnowfallAmount.Value.Value}\" snow");
            }

            if (nightPeriod?.SnowfallAmount?.Value.HasValue == true)
            {
                total += nightPeriod.SnowfallAmount.Value.Value;
                Console.WriteLine($"    Night period has {nightPeriod.SnowfallAmount.Value.Value}\" snow");
            }

            // If NWS doesn't provide snowfall amounts, try to parse from DetailedForecast
            if (total == 0)
            {
                total = ParseSnowFromText(dayPeriod.DetailedForecast, dayPeriod.ShortForecast);
                if (nightPeriod != null && total == 0)
                {
                    total = ParseSnowFromText(nightPeriod.DetailedForecast, nightPeriod.ShortForecast);
                }
            }

            return total;
        }

        /// <summary>
        /// Parse snow amounts from forecast text when NWS doesn't provide structured data
        /// </summary>
        private double ParseSnowFromText(string detailedForecast, string shortForecast)
        {
            var text = (detailedForecast + " " + shortForecast).ToLower();

            // Look for patterns like "4 to 8 inches", "6 inches", "heavy snow"
            var patterns = new[]
            {
                @"(\d+)\s*to\s*(\d+)\s*inch", // "4 to 8 inches"
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

            // Qualitative estimates based on keywords
            if (text.Contains("heavy snow"))
            {
                Console.WriteLine($"    Estimated from 'heavy snow': 6\"");
                return 6.0;
            }
            else if (text.Contains("moderate snow") || text.Contains("snow likely"))
            {
                Console.WriteLine($"    Estimated from 'moderate snow': 3\"");
                return 3.0;
            }
            else if (text.Contains("light snow") || text.Contains("chance snow"))
            {
                Console.WriteLine($"    Estimated from 'light snow': 1\"");
                return 1.0;
            }

            return 0;
        }

        /// <summary>
        /// Extract ice accumulation from day and night periods
        /// </summary>
        private double ExtractIceAmount(Period dayPeriod, Period? nightPeriod)
        {
            double total = 0;

            if (dayPeriod.IceAccumulation?.Value.HasValue == true)
                total += dayPeriod.IceAccumulation.Value.Value;

            if (nightPeriod?.IceAccumulation?.Value.HasValue == true)
                total += nightPeriod.IceAccumulation.Value.Value;

            return total;
        }

        /// <summary>
        /// Get active winter weather alerts for location
        /// </summary>
        public async Task<List<WeatherAlert>> GetActiveAlertsAsync(double latitude, double longitude)
        {
            try
            {
                var url = $"https://api.weather.gov/alerts/active?point={latitude:F4},{longitude:F4}";
                var response = await _httpClient.GetFromJsonAsync<NWSAlertsResponse>(url);

                if (response?.Features == null) return new List<WeatherAlert>();

                var winterKeywords = new[] { "winter", "snow", "ice", "blizzard", "freezing" };

                return response.Features
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
            }
            catch
            {
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
        /// Get city name from ZIP code (for saving locations)
        /// </summary>
        public async Task<(string cityName, string state)> GetCityNameFromZip(string zipCode)
        {
            try
            {
                var url = $"https://api.zippopotam.us/us/{zipCode}";
                var response = await _httpClient.GetFromJsonAsync<JsonElement>(url);

                if (response.TryGetProperty("places", out var places) && places.GetArrayLength() > 0)
                {
                    var place = places[0];
                    var city = place.GetProperty("place name").GetString() ?? "";
                    var state = place.GetProperty("state abbreviation").GetString() ?? "";
                    return (city, state);
                }
            }
            catch { }

            return ("Unknown City", "");
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
        /// Get weather forecast (alternative method signature for compatibility)
        /// </summary>
        public async Task<(List<Period>? periods, string city, string state)> GetWeatherForecast(double latitude, double longitude)
        {
            try
            {
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
                var forecastResponse = await _httpClient.GetFromJsonAsync<NWSForecastResponse>(forecastUrl);

                if (forecastResponse?.Properties?.Periods == null)
                {
                    return (null, city, state);
                }

                return (forecastResponse.Properties.Periods, city, state);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Weather forecast fetch failed: {ex.Message}");
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
        /// Get recent weather data from Open-Meteo Historical API
        /// </summary>
        public async Task<List<HistoricalWeatherDay>> GetRecentWeather(
            double latitude,
            double longitude,
            int daysBack)
        {
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

                return historicalDays;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Historical weather fetch failed: {ex.Message}");
                return new();
            }
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
