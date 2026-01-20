using System.Net.Http.Json;
using System.Text.Json;
using SnowDayPredictor.Models;

namespace SnowDayPredictor.Services
{
    public class WeatherService
    {
        private readonly HttpClient _httpClient;

        public WeatherService(HttpClient httpClient)
        {
            _httpClient = httpClient;
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SnowDayPredictor/1.0");
        }

        public async Task<(double lat, double lon)?> GetCoordinatesFromZip(string zipCode)
        {
            try
            {
                Console.WriteLine($"Geocoding ZIP: {zipCode}");

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "SnowDayPredictor/1.0");

                var url = $"https://nominatim.openstreetmap.org/search?postalcode={zipCode}&country=US&format=json&limit=1";

                var response = await _httpClient.GetAsync(url);
                Console.WriteLine($"Geocoding response status: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("Geocoding failed, trying fallback");
                    return null;
                }

                var content = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"Geocoding response: {content}");

                var data = await response.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>();

                if (data != null && data.Count > 0)
                {
                    var lat = data[0]["lat"].GetString();
                    var lon = data[0]["lon"].GetString();
                    Console.WriteLine($"Coordinates found: {lat}, {lon}");
                    return (double.Parse(lat!), double.Parse(lon!));
                }

                Console.WriteLine("No geocoding results found");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Geocoding error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
            return null;
        }

        public async Task<NWSForecastResponse?> GetWeatherForecast(double lat, double lon)
        {
            try
            {
                // First, get the forecast URL for this location
                var pointsUrl = $"https://api.weather.gov/points/{lat:F4},{lon:F4}";
                Console.WriteLine($"Calling NWS points API: {pointsUrl}");

                var pointsResponse = await _httpClient.GetAsync(pointsUrl);
                Console.WriteLine($"Points API status: {pointsResponse.StatusCode}");

                var pointsContent = await pointsResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Points API response (first 500 chars): {pointsContent.Substring(0, Math.Min(500, pointsContent.Length))}");

                if (!pointsResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Points API failed with status: {pointsResponse.StatusCode}");
                    return null;
                }

                var pointsData = await pointsResponse.Content.ReadFromJsonAsync<NWSPointsResponse>();

                if (pointsData?.Properties?.Forecast == null)
                {
                    Console.WriteLine("No forecast URL in points response");
                    return null;
                }

                Console.WriteLine($"Forecast URL: {pointsData.Properties.Forecast}");

                // Then get the actual forecast
                var forecastResponse = await _httpClient.GetAsync(pointsData.Properties.Forecast);
                Console.WriteLine($"Forecast API status: {forecastResponse.StatusCode}");

                var forecastContent = await forecastResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"Forecast response (first 500 chars): {forecastContent.Substring(0, Math.Min(500, forecastContent.Length))}");

                var forecast = await forecastResponse.Content.ReadFromJsonAsync<NWSForecastResponse>();

                Console.WriteLine($"Parsed forecast periods: {forecast?.Properties?.Periods?.Count ?? 0}");
                return forecast;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Weather API error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        public List<SnowDayForecast> CalculateSnowDayChances(NWSForecastResponse forecast, GeographyContext geography)
        {
            var results = new List<SnowDayForecast>();
            var periods = forecast.Properties.Periods.ToList();

            Console.WriteLine($"Total periods available: {periods.Count}");

            // Process each daytime period
            for (int i = 0; i < periods.Count && results.Count < 7; i++)
            {
                var period = periods[i];

                // Only process daytime periods
                if (!period.IsDaytime)
                    continue;

                Console.WriteLine($"Processing daytime period: {period.Name}");

                // Find the night before this day (if it exists)
                Period? nightBefore = null;
                if (i > 0 && !periods[i - 1].IsDaytime)
                {
                    nightBefore = periods[i - 1];
                    Console.WriteLine($"Found night before: {nightBefore.Name}");
                }

                var analysis = AnalyzePeriod(period, nightBefore, geography);


                results.Add(new SnowDayForecast
                {
                    DayName = period.Name,
                    Date = period.StartTime,
                    SnowDayChance = analysis.ClosureChance,
                    DelayChance = analysis.DelayChance,
                    Temperature = period.Temperature,
                    Forecast = period.DetailedForecast,
                    PrecipitationChance = period.ProbabilityOfPrecipitation?.Value ?? 0,
                    SnowfallAmount = analysis.SnowfallDisplay,
                    State = geography.State
                });
            }


            Console.WriteLine($"Generated {results.Count} forecast days");

            // Apply multi-day closure logic for snow-intolerant regions
            ApplyMultiDayClosures(results, geography);

            return results;
        }
        private (int ClosureChance, int DelayChance, string? SnowfallDisplay) AnalyzePeriod(
            Period day, Period? nightBefore, GeographyContext geography)
        {
            int closureChance = 0;
            int delayChance = 0;

            var dayTemp = day.Temperature;
            var dayPrecip = day.ProbabilityOfPrecipitation?.Value ?? 0;
            var dayText = day.DetailedForecast.ToLower();
            var dayShort = day.ShortForecast.ToLower();

            bool hasSnow = dayShort.Contains("snow") || dayShort.Contains("blizzard");
            bool hasIce = dayText.Contains("freezing") || dayText.Contains("ice") || dayText.Contains("sleet");

            // Analyze overnight conditions (critical for morning delays/closures)
            int overnightImpact = 0;
            if (nightBefore != null)
            {
                var nightText = nightBefore.DetailedForecast.ToLower();
                var nightShort = nightBefore.ShortForecast.ToLower();
                bool nightSnow = nightShort.Contains("snow") || nightShort.Contains("blizzard");
                bool nightIce = nightText.Contains("freezing") || nightText.Contains("ice");

                if (nightSnow || nightIce)
                {
                    overnightImpact = nightSnow ? 25 : 20;
                    Console.WriteLine($"Overnight impact detected: {overnightImpact}");
                }
            }

            // Parse snowfall amount
            string? snowfallDisplay = null;
            double snowfallInches = 0;

            if (day.SnowfallAmount?.Value.HasValue == true)
            {
                snowfallInches = day.SnowfallAmount.Value.Value * 39.3701;
                if (snowfallInches >= 0.1)
                    snowfallDisplay = $"{snowfallInches:F1}\"";
            }
            else
            {
                snowfallDisplay = ParseSnowfallFromText(dayText);
                // Try to estimate inches from qualitative descriptions
                if (snowfallDisplay == "Heavy") snowfallInches = 6;
                else if (snowfallDisplay == "Moderate") snowfallInches = 2;
                else if (snowfallDisplay == "Light") snowfallInches = 0.5;
            }

            // === CLOSURE CALCULATION ===
            if (hasSnow || hasIce)
            {
                // Base chance
                closureChance = hasIce ? 30 : 20;

                // Snowfall amount impact
                if (snowfallInches >= 8) closureChance += 50;
                else if (snowfallInches >= 6) closureChance += 40;
                else if (snowfallInches >= 4) closureChance += 30;
                else if (snowfallInches >= 2) closureChance += 20;
                else if (snowfallInches >= 1) closureChance += 10;

                // Temperature impact (colder = more dangerous)
                if (dayTemp <= 20) closureChance += 15;
                else if (dayTemp <= 25) closureChance += 10;
                else if (dayTemp <= 30) closureChance += 5;

                // Precipitation probability
                if (dayPrecip >= 80) closureChance += 20;
                else if (dayPrecip >= 60) closureChance += 15;
                else if (dayPrecip >= 40) closureChance += 10;

                // Severity keywords
                if (dayText.Contains("heavy") || dayText.Contains("blizzard")) closureChance += 25;
                if (dayText.Contains("freezing rain")) closureChance += 30;

                // Overnight impact
                closureChance += overnightImpact;

                // Geography adjustment
                closureChance = (closureChance * geography.SnowToleranceMultiplier) / 100;

                Console.WriteLine($"Base closure chance: {closureChance}");
            }

            // === DELAY CALCULATION ===
            // Delays are more common than closures for marginal conditions
            if (hasSnow || hasIce || overnightImpact > 0)
            {
                delayChance = closureChance / 2; // Start with half the closure chance

                // Conditions more likely to cause delays than closures
                if (snowfallInches >= 0.5 && snowfallInches < 3) delayChance += 20;
                if (dayTemp > 28 && dayTemp <= 33) delayChance += 15; // Marginal temps
                if (dayText.Contains("mixed") || dayText.Contains("wintry mix")) delayChance += 20;
                if (overnightImpact > 0 && closureChance < 40) delayChance += 25;

                // If closure chance is high, delay chance should be too
                if (closureChance >= 60) delayChance = Math.Max(delayChance, 75);
                else if (closureChance >= 40) delayChance = Math.Max(delayChance, 60);

                Console.WriteLine($"Delay chance: {delayChance}");
            }

            // Cap percentages
            closureChance = Math.Min(closureChance, 95);
            delayChance = Math.Min(delayChance, 95);

            return (closureChance, delayChance, snowfallDisplay);
        }

        private void ApplyMultiDayClosures(List<SnowDayForecast> forecasts, GeographyContext geography)
        {
            // In snow-intolerant areas, a major snow event can cause multi-day closures
            if (geography.TypicalClosureDays <= 1) return;

            for (int i = 0; i < forecasts.Count; i++)
            {
                if (forecasts[i].SnowDayChance >= 70)
                {
                    // Boost subsequent days
                    for (int j = 1; j < geography.TypicalClosureDays && i + j < forecasts.Count; j++)
                    {
                        var carryoverChance = (int)(forecasts[i].SnowDayChance * 0.6 / j);
                        forecasts[i + j].SnowDayChance = Math.Max(forecasts[i + j].SnowDayChance, carryoverChance);
                        Console.WriteLine($"Applied carryover: {forecasts[i + j].DayName} now {forecasts[i + j].SnowDayChance}%");
                    }
                }
            }
        }
        private string? ParseSnowfallFromText(string forecastText)
        {
            Console.WriteLine($"Parsing snowfall from: {forecastText}");

            var lowerText = forecastText.ToLower();

            // First try to find specific measurements
            var patterns = new[]
            {
        @"(\d+\.?\d*)\s*to\s*(\d+\.?\d*)\s*inch",
        @"around\s*(\d+\.?\d*)\s*inch",
        @"up\s*to\s*(\d+\.?\d*)\s*inch",
        @"(\d+\.?\d*)\s*inch",
        @"new\s*snow\s*accumulation\s*of\s*(\d+\.?\d*)\s*to\s*(\d+\.?\d*)\s*inch",
        @"accumulation\s*of\s*(\d+\.?\d*)\s*to\s*(\d+\.?\d*)\s*inch",
        @"(\d+\.?\d*)\s*to\s*(\d+\.?\d*)\s*feet"
    };

            foreach (var pattern in patterns)
            {
                var match = System.Text.RegularExpressions.Regex.Match(lowerText, pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    Console.WriteLine($"Match found with pattern: {pattern}");
                    if (match.Groups.Count == 3 && match.Groups[2].Success)
                    {
                        // Range like "3 to 5"
                        var low = match.Groups[1].Value;
                        var high = match.Groups[2].Value;

                        // Check if it's feet
                        if (pattern.Contains("feet"))
                        {
                            var result = $"{(double.Parse(low) * 12):F0}-{(double.Parse(high) * 12):F0}\"";
                            Console.WriteLine($"Returning feet converted: {result}");
                            return result;
                        }

                        var rangeResult = $"{low}-{high}\"";
                        Console.WriteLine($"Returning range: {rangeResult}");
                        return rangeResult;
                    }
                    else if (match.Groups.Count >= 2)
                    {
                        // Single value
                        var result = $"{match.Groups[1].Value}\"";
                        Console.WriteLine($"Returning single: {result}");
                        return result;
                    }
                }
            }

            // If no specific amount, look for qualitative descriptions
            if (lowerText.Contains("heavy snow") || lowerText.Contains("blizzard"))
            {
                Console.WriteLine("Found heavy snow indicator");
                return "Heavy";
            }

            if (lowerText.Contains("snow showers") || lowerText.Contains("flurries"))
            {
                Console.WriteLine("Found light snow indicator");
                return "Light";
            }

            if (lowerText.Contains("snow") && !lowerText.Contains("chance"))
            {
                Console.WriteLine("Found snow without chance - moderate");
                return "Moderate";
            }

            Console.WriteLine("No snowfall pattern matched");
            return null;
        }

        public async Task<(string cityName, string state)> GetCityNameFromZip(string zipCode)
        {
            // Try 1: Census.gov (most reliable, government source)
            try
            {
                Console.WriteLine("Trying Census.gov geocoder...");

                var censusUrl = $"https://geocoding.geo.census.gov/geocoder/locations/address?zip={zipCode}&benchmark=2020&format=json";

                var censusResponse = await _httpClient.GetAsync(censusUrl);
                Console.WriteLine($"Census status: {censusResponse.StatusCode}");

                if (censusResponse.IsSuccessStatusCode)
                {
                    var content = await censusResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"Census response: {content.Substring(0, Math.Min(300, content.Length))}");

                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("result", out var result))
                    {
                        if (result.TryGetProperty("addressMatches", out var matches) && matches.GetArrayLength() > 0)
                        {
                            var match = matches[0];
                            if (match.TryGetProperty("addressComponents", out var components))
                            {
                                string? city = null;
                                string? state = null;

                                if (components.TryGetProperty("city", out var cityProp))
                                    city = cityProp.GetString();

                                if (components.TryGetProperty("state", out var stateProp))
                                    state = stateProp.GetString();

                                if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(state))
                                {
                                    Console.WriteLine($"✓ Census success: {city}, {state}");
                                    return ($"{city}, {state}", state);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Census error: {ex.Message}");
            }

            // Try 2: Zippopotam.us (backup)
            try
            {
                Console.WriteLine("Trying Zippopotam.us...");

                var url = $"https://api.zippopotam.us/us/{zipCode}";

                var response = await _httpClient.GetAsync(url);
                Console.WriteLine($"Zippopotam status: {response.StatusCode}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Zippopotam response: {content.Substring(0, Math.Min(300, content.Length))}");

                    using var doc = System.Text.Json.JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("places", out var places) && places.GetArrayLength() > 0)
                    {
                        var place = places[0];

                        string? city = null;
                        string? stateAbbr = null;

                        if (place.TryGetProperty("place name", out var placeNameElement))
                            city = placeNameElement.GetString();

                        if (place.TryGetProperty("state abbreviation", out var stateAbbrElement))
                            stateAbbr = stateAbbrElement.GetString();

                        if (!string.IsNullOrEmpty(city) && !string.IsNullOrEmpty(stateAbbr))
                        {
                            Console.WriteLine($"✓ Zippopotam success: {city}, {stateAbbr}");
                            return ($"{city}, {stateAbbr}", stateAbbr);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"✗ Zippopotam error: {ex.Message}");
            }

            // Fallback: Use ZIP prefix to at least get the state
            var stateFromZip = GetStateFromZipPrefix(zipCode);
            if (!string.IsNullOrEmpty(stateFromZip))
            {
                Console.WriteLine($"✓ Using ZIP prefix fallback: {zipCode} → {stateFromZip}");
                return ($"ZIP {zipCode}", stateFromZip);
            }

            // Complete failure - return ZIP with no state
            Console.WriteLine($"✗ All lookups failed for ZIP: {zipCode}");
            return ($"ZIP {zipCode}", "");
        }

        private string GetStateFromZipPrefix(string zipCode)
        {
            if (zipCode.Length < 3) return "";

            var prefix = int.Parse(zipCode.Substring(0, 3));

            // ZIP code ranges by state (simplified)
            return prefix switch
            {
                >= 010 and <= 027 => "MA",
                >= 028 and <= 029 => "RI",
                >= 030 and <= 038 => "NH",
                >= 039 and <= 049 => "ME",
                >= 050 and <= 059 => "VT",
                >= 060 and <= 069 => "CT",
                >= 070 and <= 089 => "NJ",
                >= 100 and <= 149 => "NY",
                >= 150 and <= 196 => "PA",
                >= 197 and <= 199 => "DE",
                >= 200 and <= 205 => "DC",
                >= 206 and <= 219 => "MD",
                >= 220 and <= 246 => "VA",
                >= 247 and <= 268 => "WV",
                >= 270 and <= 289 => "NC",
                >= 290 and <= 299 => "SC",
                >= 300 and <= 319 => "GA",
                >= 320 and <= 349 => "FL",
                >= 350 and <= 369 => "AL",
                >= 370 and <= 385 => "TN",
                >= 386 and <= 397 => "MS",
                >= 398 and <= 399 => "GA",
                >= 400 and <= 427 => "KY",
                >= 430 and <= 458 => "OH",
                >= 460 and <= 479 => "IN",
                >= 480 and <= 499 => "MI",
                >= 500 and <= 528 => "IA",
                >= 530 and <= 549 => "WI",
                >= 550 and <= 567 => "MN",
                >= 570 and <= 577 => "SD",
                >= 580 and <= 588 => "ND",
                >= 590 and <= 599 => "MT",
                >= 600 and <= 620 => "IL",
                >= 622 and <= 629 => "IL",
                >= 630 and <= 658 => "MO",
                >= 660 and <= 679 => "KS",
                >= 680 and <= 693 => "NE",
                >= 700 and <= 714 => "LA",
                >= 716 and <= 729 => "AR",
                >= 730 and <= 749 => "OK",
                >= 750 and <= 799 => "TX",
                >= 800 and <= 816 => "CO",
                >= 820 and <= 831 => "WY",
                >= 832 and <= 838 => "ID",
                >= 840 and <= 847 => "UT",
                >= 850 and <= 865 => "AZ",
                >= 870 and <= 884 => "NM",
                >= 885 and <= 898 => "TX",
                >= 889 and <= 899 => "NV",
                >= 900 and <= 961 => "CA",
                >= 962 and <= 966 => "HI",
                >= 967 and <= 969 => "HI",
                >= 970 and <= 979 => "OR",
                >= 980 and <= 994 => "WA",
                >= 995 and <= 999 => "AK",
                _ => ""
            };
        }

        public async Task<(string cityName, string state)> GetCityNameFromCoordinates(double lat, double lon)
        {
            try
            {
                var url = $"https://nominatim.openstreetmap.org/reverse?lat={lat}&lon={lon}&format=json";
                var response = await _httpClient.GetFromJsonAsync<Dictionary<string, JsonElement>>(url);

                if (response != null && response.ContainsKey("address"))
                {
                    var address = response["address"];

                    string? city = null;
                    string? state = null;

                    // Try city, town, village in order
                    if (address.TryGetProperty("city", out var cityProp))
                        city = cityProp.GetString();
                    else if (address.TryGetProperty("town", out var town))
                        city = town.GetString();
                    else if (address.TryGetProperty("village", out var village))
                        city = village.GetString();
                    else if (address.TryGetProperty("county", out var county))
                    {
                        var countyStr = county.GetString() ?? "";
                        city = countyStr.Replace(" County", "");
                    }

                    // Get state abbreviation
                    if (address.TryGetProperty("state", out var stateProp))
                    {
                        var stateName = stateProp.GetString() ?? "";
                        state = GetStateAbbreviation(stateName);
                    }

                    if (!string.IsNullOrEmpty(city))
                    {
                        return (city, state ?? "");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Reverse geocoding error: {ex.Message}");
            }
            return ("Current Location", "");
        }

        private string GetStateAbbreviation(string stateName)
        {
            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

            return states.TryGetValue(stateName, out var abbr) ? abbr : stateName;
        }
    }
}