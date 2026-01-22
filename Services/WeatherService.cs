using SnowDayPredictor.Models;
using System.Net.Http.Json;
using System.Text.Json;
using static SnowDayPredictor.Models.GeographyContext;

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

        public async Task<(NWSForecastResponse? forecast, string city, string state)> GetWeatherForecast(double lat, double lon)
        {
            try
            {
                // First, get the forecast URL for this location
                var pointsUrl = $"https://api.weather.gov/points/{lat:F4},{lon:F4}";
                Console.WriteLine($"Calling NWS points API: {pointsUrl}");

                var pointsResponse = await _httpClient.GetAsync(pointsUrl);
                Console.WriteLine($"Points API status: {pointsResponse.StatusCode}");

                if (!pointsResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Points API failed with status: {pointsResponse.StatusCode}");
                    return (null, "", "");
                }

                var pointsData = await pointsResponse.Content.ReadFromJsonAsync<NWSPointsResponse>();

                if (pointsData?.Properties?.Forecast == null)
                {
                    Console.WriteLine("No forecast URL in points response");
                    return (null, "", "");
                }

                // Extract city and state from NWS response
                string city = pointsData.Properties.RelativeLocation?.Properties?.City ?? "";
                string state = pointsData.Properties.RelativeLocation?.Properties?.State ?? "";

                Console.WriteLine($"NWS location data: {city}, {state}");

                // Then get the actual forecast
                var forecastResponse = await _httpClient.GetAsync(pointsData.Properties.Forecast);
                Console.WriteLine($"Forecast API status: {forecastResponse.StatusCode}");

                var forecast = await forecastResponse.Content.ReadFromJsonAsync<NWSForecastResponse>();

                Console.WriteLine($"Parsed forecast periods: {forecast?.Properties?.Periods?.Count ?? 0}");

                return (forecast, city, state);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Weather API error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return (null, "", "");
            }
        }


        public List<SnowDayForecast> CalculateSnowDayChances(
        NWSForecastResponse forecast,
        GeographyContext geography,
        List<HistoricalWeatherDay> recentHistory,
        List<WeatherAlert> activeAlerts)
        {
            var results = new List<SnowDayForecast>();
            var periods = forecast.Properties.Periods.ToList();

            Console.WriteLine($"Total periods available: {periods.Count}");
            Console.WriteLine($"Historical events to consider: {recentHistory.Count(h => h.SnowfallInches > 2)}");

            // First pass: Analyze ALL days (including weekends for aftermath tracking)
            var allDays = new List<(Period period, int closure, int delay, double snowfall, string? display)>();

            for (int i = 0; i < periods.Count; i++)
            {
                var period = periods[i];
                if (!period.IsDaytime) continue;

                var dayOfWeek = period.StartTime.DayOfWeek;
                var isWeekend = dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday;

                Console.WriteLine($"\n=== Analyzing: {period.Name} ({period.StartTime:MM/dd}) {(isWeekend ? "[WEEKEND]" : "")} ===");

                Period? nightBefore = i > 0 && !periods[i - 1].IsDaytime ? periods[i - 1] : null;
                var analysis = AnalyzePeriod(period, nightBefore, geography, activeAlerts);

                allDays.Add((period, analysis.ClosureChance, analysis.DelayChance,
                            analysis.SnowfallInches, analysis.SnowfallDisplay));
            }

            Console.WriteLine($"\nTotal days analyzed: {allDays.Count}");

            // Second pass: Apply multi-day impacts to ALL days (including weekends)
            for (int i = 0; i < allDays.Count; i++)
            {
                var current = allDays[i];
                var currentDate = current.period.StartTime.Date;
                var isWeekend = current.period.StartTime.DayOfWeek == DayOfWeek.Saturday ||
                                current.period.StartTime.DayOfWeek == DayOfWeek.Sunday;

                // Check historical aftermath
                var historicalAftermath = CalculateAftermathImpact(currentDate, recentHistory, geography);

                // Check forecast-based aftermath (look backward at ALL previous days, including weekends)
                var forecastAftermath = CalculateForecastAftermath(i, allDays, geography, activeAlerts);

                // Take the maximum of all impacts
                var finalClosure = Math.Max(current.closure,
                                           Math.Max(historicalAftermath.ClosureChance,
                                                   forecastAftermath.ClosureChance));
                var finalDelay = Math.Max(current.delay,
                                         Math.Max(historicalAftermath.DelayChance,
                                                 forecastAftermath.DelayChance));

                if (historicalAftermath.ClosureChance > 0 || historicalAftermath.DelayChance > 0)
                {
                    Console.WriteLine($"Historical aftermath: +{historicalAftermath.ClosureChance}% closure, +{historicalAftermath.DelayChance}% delay");
                }
                if (forecastAftermath.ClosureChance > 0 || forecastAftermath.DelayChance > 0)
                {
                    Console.WriteLine($"Forecast aftermath: +{forecastAftermath.ClosureChance}% closure, +{forecastAftermath.DelayChance}% delay");
                }

                // Add ALL days (including weekends) to results
                results.Add(new SnowDayForecast
                {
                    DayName = current.period.Name,
                    Date = current.period.StartTime,
                    SnowDayChance = finalClosure,
                    DelayChance = finalDelay,
                    Temperature = current.period.Temperature,
                    Forecast = current.period.DetailedForecast,
                    PrecipitationChance = current.period.ProbabilityOfPrecipitation?.Value ?? 0,
                    SnowfallAmount = current.display,
                    State = geography.State
                });

                // Stop after we have 7 days total
                if (results.Count >= 7) break;
            }

            Console.WriteLine($"\nGenerated {results.Count} weekday forecasts");
            return results;
        }


        private (int ClosureChance, int DelayChance) CalculateForecastAftermath(
            int currentIndex,
            List<(Period period, int closure, int delay, double snowfall, string? display)> forecastDays,
            GeographyContext geography,
            List<WeatherAlert> activeAlerts)
        {
            int maxClosureAftermath = 0;
            int maxDelayAftermath = 0;

            // Look back at previous forecast days (up to 5 days)
            for (int lookback = 1; lookback <= Math.Min(5, currentIndex); lookback++)
            {
                var previousDay = forecastDays[currentIndex - lookback];
                var previousDate = previousDay.period.StartTime.Date;
                var currentDate = forecastDays[currentIndex].period.StartTime.Date;
                var daysSince = (currentDate - previousDate).Days;

                // Only consider if it was a significant event
                bool significantEvent = previousDay.closure >= 70 || previousDay.snowfall >= 4.0;

                // Detect MAJOR events (8+ inches or 85%+ closure)
                bool majorEvent = previousDay.snowfall >= 8.0 || previousDay.closure >= 85;

                // Check if there was an active watch or warning during the event
                bool hadActiveAlert = activeAlerts.Any(a =>
                {
                    if (a.Severity < AlertSeverity.Watch) return false;  // Include watches and warnings
                    if (!a.Onset.HasValue) return false;

                    DateTime effectiveEnd = a.Ends ?? a.Expires ?? DateTime.MaxValue;

                    return previousDate >= a.Onset.Value.Date &&
                           previousDate <= effectiveEnd.Date;
                });

                // For major events, treat watches like warnings (they usually upgrade)
                bool hadMajorEventWithAlert = hadActiveAlert && majorEvent;

                if (hadActiveAlert && !significantEvent)
                {
                    significantEvent = previousDay.closure >= 60 || previousDay.snowfall >= 3.0;
                    Console.WriteLine("Active alert lowers threshold for significant event");
                }

                // Log major events
                if (hadMajorEventWithAlert)
                {
                    Console.WriteLine($"MAJOR EVENT with official alert: {previousDay.snowfall:F1}\" snow, {previousDay.closure}% closure");
                }

                if (!significantEvent) continue;

                Console.WriteLine($"Found significant event on {previousDate:MM/dd}: {previousDay.snowfall:F1}\" snow, {previousDay.closure}% closure");

                // Check if temperatures have stayed cold (below freezing)
                bool coldPersists = CheckColdPersistence(currentIndex - lookback, currentIndex, forecastDays);

                int closureAftermath = 0;
                int delayAftermath = 0;

                // Calculate base aftermath - MAJOR EVENT AWARE
                if (daysSince == 1) // Day after event
                {
                    if (majorEvent)
                    {
                        closureAftermath = hadMajorEventWithAlert ? 70 : 60;
                        delayAftermath = hadMajorEventWithAlert ? 85 : 75;
                    }
                    else if (previousDay.snowfall >= 6 || previousDay.closure >= 75)
                    {
                        closureAftermath = 50;
                        delayAftermath = 65;
                    }
                    else if (previousDay.closure >= 70)
                    {
                        closureAftermath = 35;
                        delayAftermath = 55;
                    }
                    else
                    {
                        closureAftermath = 25;
                        delayAftermath = 45;
                    }
                }
                else if (daysSince == 2) // 2 days after
                {
                    if (majorEvent)
                    {
                        closureAftermath = hadMajorEventWithAlert ? 70 : 60;
                        delayAftermath = hadMajorEventWithAlert ? 85 : 75;
                    }
                    else if (previousDay.snowfall >= 6)
                    {
                        closureAftermath = 35;
                        delayAftermath = 50;
                    }
                    else
                    {
                        closureAftermath = 20;
                        delayAftermath = 35;
                    }
                }
                else if (daysSince == 3) // 3 days after
                {
                    if (majorEvent)
                    {
                        closureAftermath = hadMajorEventWithAlert ? 35 : 25;  // Reduced from 45/35
                        delayAftermath = hadMajorEventWithAlert ? 50 : 40;    // Reduced from 60/50
                    }
                    else if (previousDay.snowfall >= 6)
                    {
                        closureAftermath = 18;
                        delayAftermath = 30;
                    }
                    else
                    {
                        closureAftermath = 8;
                        delayAftermath = 18;
                    }
                }
                else if (daysSince == 4) // 4 days after
                {
                    if (majorEvent)
                    {
                        closureAftermath = hadMajorEventWithAlert ? 25 : 18;  // Reduced from 30/20
                        delayAftermath = hadMajorEventWithAlert ? 38 : 30;    // Reduced from 45/35
                    }
                    else if (previousDay.snowfall >= 8)
                    {
                        closureAftermath = 8;
                        delayAftermath = 18;
                    }
                    else
                    {
                        closureAftermath = 5;
                        delayAftermath = 10;
                    }
                }
                else if (daysSince == 5) // 5 days after (rare)
                {
                    if (majorEvent && hadMajorEventWithAlert)
                    {
                        closureAftermath = 15;
                        delayAftermath = 25;
                    }
                    else if (previousDay.snowfall >= 10)
                    {
                        closureAftermath = 5;
                        delayAftermath = 12;
                    }
                }

                // Cold temps extend impact
                if (coldPersists)
                {
                    delayAftermath = (int)(delayAftermath * 1.25);
                    closureAftermath = (int)(closureAftermath * 1.15);
                    Console.WriteLine($"Cold temps persisting - boosting aftermath impact");
                }

                // Geography adjustment - REGIONAL
                if (geography.SnowToleranceMultiplier >= 200) // Deep South
                {
                    if (majorEvent)
                    {
                        closureAftermath = (int)(closureAftermath * 2.0);
                        delayAftermath = (int)(delayAftermath * 1.8);
                    }
                    else
                    {
                        closureAftermath = (int)(closureAftermath * 1.7);
                        delayAftermath = (int)(delayAftermath * 1.5);
                    }
                }
                else if (geography.SnowToleranceMultiplier >= 150) // Mid-South (VA, MD, etc.)
                {
                    if (majorEvent)
                    {
                        closureAftermath = (int)(closureAftermath * 1.4);
                        delayAftermath = (int)(delayAftermath * 1.3);
                    }
                    else
                    {
                        closureAftermath = (int)(closureAftermath * 1.2);
                        delayAftermath = (int)(delayAftermath * 1.15);
                    }
                }
                else if (geography.SnowToleranceMultiplier <= 75) // Northern regions
                {
                    closureAftermath = (int)(closureAftermath * 0.75);
                    delayAftermath = (int)(delayAftermath * 0.8);
                }

                // Cap individual aftermath contributions based on days since event
                // This allows Day 1-2 to be high while ensuring Day 3+ decays naturally
                int closureCap = daysSince switch
                {
                    1 => 85,  // Day 1: High impacts expected
                    2 => 80,  // Day 2: Still very high
                    3 => 70,  // Day 3: Starting to improve
                    4 => 60,  // Day 4: Continued improvement
                    _ => 50   // Day 5+: Lower impacts
                };

                int delayCap = closureCap + 5;  // Delays tend to persist slightly longer

                closureAftermath = Math.Min(closureAftermath, closureCap);
                delayAftermath = Math.Min(delayAftermath, delayCap);

                // Track the maximum aftermath from any previous event
                maxClosureAftermath = Math.Max(maxClosureAftermath, closureAftermath);
                maxDelayAftermath = Math.Max(maxDelayAftermath, delayAftermath);

                Console.WriteLine($"Aftermath from {previousDate:MM/dd} ({daysSince} days ago): +{closureAftermath}% closure, +{delayAftermath}% delay");
            }

            return (Math.Min(maxClosureAftermath, 95), Math.Min(maxDelayAftermath, 95));
        }

        private bool CheckColdPersistence(int startIndex, int endIndex,
            List<(Period period, int closure, int delay, double snowfall, string? display)> forecastDays)
        {
            // Check if temps have stayed at or below freezing for most of the period
            int coldDays = 0;
            int totalDays = endIndex - startIndex + 1;

            for (int i = startIndex; i <= endIndex; i++)
            {
                if (forecastDays[i].period.Temperature <= 32)
                {
                    coldDays++;
                }
            }

            // If 70%+ of days were at/below freezing, cold persists
            bool persists = (coldDays / (double)totalDays) >= 0.7;

            if (persists)
            {
                Console.WriteLine($"Cold persistence detected: {coldDays}/{totalDays} days below freezing");
            }

            return persists;
        }

        private (int ClosureChance, int DelayChance) CalculateAftermathImpact(
    DateTime forecastDate,
    List<HistoricalWeatherDay> recentHistory,
    GeographyContext geography)
        {
            // Find recent significant snow events (4+ inches)
            var significantEvents = recentHistory
                .Where(h => h.SnowfallInches >= 4.0)
                .OrderByDescending(h => h.Date)
                .ToList();

            if (!significantEvents.Any())
                return (0, 0);

            var mostRecentEvent = significantEvents.First();
            var daysSinceEvent = (forecastDate.Date - mostRecentEvent.Date).Days;

            // Only consider events from last 3 days
            if (daysSinceEvent < 1 || daysSinceEvent > 3)
                return (0, 0);

            Console.WriteLine($"Aftermath from {mostRecentEvent.Date:MM/dd}: {mostRecentEvent.SnowfallInches:F1}\" snow, {daysSinceEvent} days ago");

            int closureChance = 0;
            int delayChance = 0;

            // Day 2 after major event (Day 1 = event day, Day 2 = next day)
            if (daysSinceEvent == 1)
            {
                if (mostRecentEvent.SnowfallInches >= 8)
                {
                    // Major event (8+ inches)
                    closureChance = 50;
                    delayChance = 70;
                }
                else if (mostRecentEvent.SnowfallInches >= 6)
                {
                    // Significant event (6-8 inches)
                    closureChance = 35;
                    delayChance = 65;
                }
                else
                {
                    // Moderate event (4-6 inches)
                    closureChance = 20;
                    delayChance = 55;
                }

                // Geography adjustment - southern states take longer to recover
                if (geography.SnowToleranceMultiplier >= 150) // Mid-South and lower
                {
                    closureChance = (int)(closureChance * 1.5);
                    delayChance = (int)(delayChance * 1.3);
                }
                else if (geography.SnowToleranceMultiplier >= 200) // Deep South
                {
                    closureChance = (int)(closureChance * 2.0);
                    delayChance = (int)(delayChance * 1.5);
                }
            }
            // Day 3 after event (mainly southern states)
            else if (daysSinceEvent == 2 && geography.SnowToleranceMultiplier >= 150)
            {
                if (mostRecentEvent.SnowfallInches >= 6)
                {
                    closureChance = 30;
                    delayChance = 40;
                }
                else
                {
                    closureChance = 15;
                    delayChance = 30;
                }
            }
            // Day 4 (only deep south with major events)
            else if (daysSinceEvent == 3 &&
                     geography.SnowToleranceMultiplier >= 200 &&
                     mostRecentEvent.SnowfallInches >= 8)
            {
                closureChance = 20;
                delayChance = 25;
            }

            // Cap at 95%
            closureChance = Math.Min(closureChance, 95);
            delayChance = Math.Min(delayChance, 95);

            return (closureChance, delayChance);
        }

        private (int ClosureChance, int DelayChance, double SnowfallInches, string? SnowfallDisplay) AnalyzePeriod(
            Period day, Period? nightBefore, GeographyContext geography, List<WeatherAlert> activeAlerts)
        {
            int closureChance = 0;
            int delayChance = 0;

            var dayTemp = day.Temperature;
            var dayPrecip = day.ProbabilityOfPrecipitation?.Value ?? 0;
            var dayText = day.DetailedForecast.ToLower();
            var dayShort = day.ShortForecast.ToLower();

            bool hasSnow = dayShort.Contains("snow") || dayShort.Contains("blizzard");
            bool hasIce = dayText.Contains("freezing") || dayText.Contains("ice") || dayText.Contains("sleet");

            // Parse snowfall amount - try multiple methods
            double snowfallInches = InferSnowfallAmount(day);
            string? snowfallDisplay = null;

            if (snowfallInches >= 0.1)
            {
                snowfallDisplay = $"{snowfallInches:F1}\"";
            }
            else
            {
                snowfallDisplay = ParseSnowfallFromText(dayText);
            }

            Console.WriteLine($"Snowfall estimate: {snowfallInches:F1}\" ({snowfallDisplay ?? "none"})");

            // ==========================================
            // INDEPENDENT CLOSURE ANALYSIS (Severity)
            // ==========================================
            if (hasSnow || hasIce)
            {
                closureChance = hasIce ? 35 : 25;

                // Snowfall amount
                if (snowfallInches >= 10) closureChance += 60;
                else if (snowfallInches >= 8) closureChance += 50;
                else if (snowfallInches >= 6) closureChance += 40;
                else if (snowfallInches >= 4) closureChance += 30;
                else if (snowfallInches >= 2) closureChance += 20;
                else if (snowfallInches >= 1) closureChance += 10;

                // Temperature danger
                if (dayTemp <= 15) closureChance += 20;
                else if (dayTemp <= 20) closureChance += 15;
                else if (dayTemp <= 25) closureChance += 10;
                else if (dayTemp <= 30) closureChance += 5;

                // Precipitation probability
                if (dayPrecip >= 80) closureChance += 20;
                else if (dayPrecip >= 60) closureChance += 15;
                else if (dayPrecip >= 40) closureChance += 10;

                // Severity keywords
                if (dayText.Contains("heavy snow")) closureChance += 25;
                if (dayText.Contains("blizzard")) closureChance += 30;
                if (dayText.Contains("freezing rain")) closureChance += 35;

                // Still snowing during school hours
                if (dayText.Contains("snow") && !dayText.Contains("chance"))
                    closureChance += 15;

                // Geography adjustment
                closureChance = (closureChance * geography.SnowToleranceMultiplier) / 100;

                // Apply alert bonuses
                foreach (var alert in activeAlerts)
                {
                    // Use 'Ends' if available (actual event end), otherwise fall back to 'Expires'
                    DateTime effectiveEnd = alert.Ends ?? alert.Expires ?? DateTime.MaxValue;

                    // Check if alert covers this day
                    if (alert.Onset.HasValue)
                    {
                        if (day.StartTime.Date >= alert.Onset.Value.Date &&
                            day.StartTime.Date <= effectiveEnd.Date)
                        {
                            int alertBonus = alert.Severity switch
                            {
                                AlertSeverity.Extreme => 60,   // Blizzard/Ice Storm Warning
                                AlertSeverity.Warning => 40,   // Winter Storm Warning
                                AlertSeverity.Watch => 25,     // Winter Storm Watch
                                AlertSeverity.Advisory => 18,  // Winter Weather Advisory
                                _ => 0
                            };

                            int delayBonus = (int)(alertBonus * 0.75); // Delays get 75% of closure bonus

                            closureChance += alertBonus;
                            delayChance += delayBonus;

                            Console.WriteLine($"Alert boost applied to {day.Name}: {alert.Type} → +{alertBonus}% closure, +{delayBonus}% delay");
                        }
                        else
                        {
                            Console.WriteLine($"Alert does not cover {day.Name} ({day.StartTime:MM/dd}) - Onset: {alert.Onset.Value:MM/dd}, End: {effectiveEnd:MM/dd}");
                        }
                    }
                }
            }

            // ==========================================
            // INDEPENDENT DELAY ANALYSIS (Time-window)
            // ==========================================

            // Check for morning ice window (independent of snow)
            bool morningIceWindow = false;
            if (nightBefore != null)
            {
                var nightTemp = nightBefore.Temperature;
                var nightText = nightBefore.DetailedForecast.ToLower();

                // Classic ice scenario: Temps cross freezing AND there was moisture
                if (nightTemp <= 32 && dayTemp >= 35)
                {
                    // CRITICAL: Only trigger if there was actual precipitation/moisture
                    bool hadPrecipitation = nightText.Contains("rain") ||
                                           nightText.Contains("snow") ||
                                           nightText.Contains("drizzle") ||
                                           nightText.Contains("sleet") ||
                                           nightText.Contains("mix") ||
                                           dayText.Contains("rain") ||
                                           dayText.Contains("drizzle");

                    // Also check for wet roads from previous conditions
                    bool wetConditions = nightText.Contains("wet") ||
                                       nightText.Contains("shower") ||
                                       nightBefore.ProbabilityOfPrecipitation?.Value > 30;

                    if (hadPrecipitation || wetConditions)
                    {
                        morningIceWindow = true;
                        Console.WriteLine("Morning ice window detected (freeze → thaw with moisture)");

                        delayChance += 60; // Base delay for ice window

                        // Recent/ongoing precipitation makes it worse
                        if (hadPrecipitation)
                        {
                            delayChance += 20; // Moisture + freeze = definite ice
                            Console.WriteLine("Precipitation detected - ice highly likely");
                        }

                        // How quickly does it warm up?
                        if (dayTemp >= 40)
                        {
                            delayChance += 10; // Fast warmup = good for delay
                        }
                        else if (dayTemp <= 36)
                        {
                            delayChance -= 10; // Slow warmup = might need closure
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Temps cross 32°F but no moisture detected - no ice risk");
                    }
                }
            }

            // Overnight snow that ended early
            if (hasSnow && nightBefore != null)
            {
                var nightText = nightBefore.DetailedForecast.ToLower();
                bool nightSnow = nightText.Contains("snow");

                if (nightSnow)
                {
                    Console.WriteLine("Overnight snow detected");

                    // Snow overnight, but stopped = delay likely
                    if (!dayText.Contains("snow") || dayText.Contains("chance"))
                    {
                        // Snow ended, roads being cleared
                        if (snowfallInches >= 1 && snowfallInches < 4)
                        {
                            delayChance += 50; // Light to moderate overnight snow
                        }
                        else if (snowfallInches >= 4 && snowfallInches < 6)
                        {
                            delayChance += 40; // Heavier, but crews have time
                        }

                        // Temperature helps or hurts
                        if (dayTemp >= 35) delayChance += 15; // Warming = melting
                        if (dayTemp <= 25) delayChance -= 10; // Cold = stays icy
                    }
                    else
                    {
                        // Still snowing into morning = bad for delay
                        delayChance += 20; // Some delay possible
                    }
                }
            }

            // Light/marginal snow conditions (delay-first approach)
            if (hasSnow && snowfallInches >= 1 && snowfallInches < 3)
            {
                delayChance += 40; // Try delay before closing

                if (geography.SnowToleranceMultiplier >= 100) // Not high-tolerance areas
                    delayChance += 20;
            }

            // Mixed precipitation = messy but often just delays
            if (dayText.Contains("mix") || dayText.Contains("wintry mix"))
            {
                delayChance += 35;
            }

            // Geography adjustment for delays
            if (geography.SnowToleranceMultiplier >= 150)
            {
                // Southern areas: any ice/snow causes delays
                delayChance = (int)(delayChance * 1.3);
            }
            else if (geography.SnowToleranceMultiplier <= 75)
            {
                // Northern areas: less likely to delay unless necessary
                delayChance = (int)(delayChance * 0.8);
            }

            // ==========================================
            // RELATIONSHIP LOGIC
            // ==========================================

            // If closure is very high, delay becomes less relevant
            if (closureChance >= 75)
            {
                delayChance = Math.Min(delayChance, 25);
                Console.WriteLine("High closure reduces delay probability (too severe)");
            }

            // If closure is moderate and delay is low, might use delay as hedge
            if (closureChance >= 40 && closureChance <= 70 && delayChance < 50)
            {
                delayChance = Math.Max(delayChance, closureChance - 15);
                Console.WriteLine("Borderline conditions - delay as hedge strategy");
            }

            // Ice with no snow = delay-heavy scenario
            if (hasIce && !hasSnow && morningIceWindow)
            {
                if (closureChance < 40)
                {
                    delayChance = Math.Max(delayChance, 65);
                    Console.WriteLine("Ice window without snow - high delay focus");
                }
            }

            // Cap percentages
            closureChance = Math.Min(closureChance, 95);
            delayChance = Math.Min(delayChance, 95);

            Console.WriteLine($"Final: {closureChance}% closure, {delayChance}% delay");

            return (closureChance, delayChance, snowfallInches, snowfallDisplay);
        }

        private double InferSnowfallAmount(Period period)
        {
            var text = period.DetailedForecast.ToLower();
            var shortText = period.ShortForecast.ToLower();
            var precipProb = period.ProbabilityOfPrecipitation?.Value ?? 0;
            var temp = period.Temperature;

            // If NWS gave us a number, use it
            if (period.SnowfallAmount?.Value.HasValue == true)
            {
                return period.SnowfallAmount.Value.Value * 39.3701; // cm to inches
            }

            // Look for explicit amounts in text
            var textAmount = ParseSnowfallFromText(text);
            if (textAmount != null)
            {
                // Check for range like "5-9"" or "5.0""
                if (textAmount.Contains("-"))
                {
                    // Extract range and use midpoint
                    var rangeMatch = System.Text.RegularExpressions.Regex.Match(textAmount, @"(\d+\.?\d*)-(\d+\.?\d*)");
                    if (rangeMatch.Success)
                    {
                        var low = double.Parse(rangeMatch.Groups[1].Value);
                        var high = double.Parse(rangeMatch.Groups[2].Value);
                        var midpoint = (low + high) / 2.0;
                        Console.WriteLine($"Snow range detected: {low}-{high}\", using midpoint: {midpoint:F1}\"");
                        return midpoint;
                    }
                }
                else if (textAmount.Contains("\""))
                {
                    // Single value
                    var match = System.Text.RegularExpressions.Regex.Match(textAmount, @"(\d+\.?\d*)");
                    if (match.Success && double.TryParse(match.Value, out var inches))
                    {
                        return inches;
                    }
                }
            }

            // Infer from conditions
            bool hasSnow = shortText.Contains("snow") && !shortText.Contains("chance");

            if (!hasSnow) return 0;

            // Heavy snow indicators
            if (text.Contains("heavy snow") || text.Contains("blizzard"))
                return 8.0;

            // High confidence + cold = significant
            if (precipProb >= 80 && temp <= 25)
                return 6.0;

            if (precipProb >= 70 && temp <= 28)
                return 4.0;

            // Moderate snow
            if (precipProb >= 60 || shortText == "snow")
                return 3.0;

            // Light snow
            if (precipProb >= 40 || shortText.Contains("snow"))
                return 1.5;

            return 0.5; // Trace
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


        public async Task<List<HistoricalWeatherDay>> GetRecentWeather(double lat, double lon, int daysBack = 3)
        {
            try
            {
                var endDate = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
                var startDate = DateTime.Today.AddDays(-daysBack).ToString("yyyy-MM-dd");

                var url = $"https://archive-api.open-meteo.com/v1/archive?" +
                          $"latitude={lat:F4}&longitude={lon:F4}&" +
                          $"start_date={startDate}&end_date={endDate}&" +
                          $"daily=temperature_2m_max,temperature_2m_min,snowfall_sum,precipitation_sum&" +
                          $"temperature_unit=fahrenheit&precipitation_unit=inch";

                Console.WriteLine($"Fetching historical weather: {url}");

                var response = await _httpClient.GetFromJsonAsync<OpenMeteoHistoricalResponse>(url);

                if (response?.Daily != null && response.Daily.Time.Count > 0)
                {
                    var result = new List<HistoricalWeatherDay>();

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

                        result.Add(day);

                        if (day.SnowfallInches > 0)
                        {
                            Console.WriteLine($"Historical: {day.Date:MM/dd} - {day.SnowfallInches:F1}\" snow, temps {day.TempMin:F0}-{day.TempMax:F0}°F");
                        }
                    }

                    Console.WriteLine($"Retrieved {result.Count} days of historical data");
                    return result;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Historical weather error: {ex.Message}");
            }

            return new List<HistoricalWeatherDay>();
        }


        public async Task<List<WeatherAlert>> GetActiveAlerts(double lat, double lon)
        {
            try
            {
                var url = $"https://api.weather.gov/alerts/active?point={lat:F4},{lon:F4}";
                Console.WriteLine($"Fetching alerts: {url}");

                var response = await _httpClient.GetAsync(url);
                Console.WriteLine($"Alerts API status: {response.StatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine("No alerts API response");
                    return new List<WeatherAlert>();
                }

                var alertsData = await response.Content.ReadFromJsonAsync<NWSAlertsResponse>();

                if (alertsData?.Features == null || !alertsData.Features.Any())
                {
                    Console.WriteLine("No active alerts");
                    return new List<WeatherAlert>();
                }

                var alerts = new List<WeatherAlert>();

                foreach (var feature in alertsData.Features)
                {
                    var props = feature.Properties;
                    var eventType = props.Event.ToLower();

                    // Only process winter weather alerts
                    if (!eventType.Contains("winter") && !eventType.Contains("snow") &&
                        !eventType.Contains("ice") && !eventType.Contains("blizzard") &&
                        !eventType.Contains("freeze") && !eventType.Contains("cold"))
                    {
                        continue;
                    }

                    var alert = new WeatherAlert
                    {
                        Type = props.Event,
                        Headline = props.Headline,
                        Description = props.Description,
                        Onset = props.Onset,
                        Expires = props.Expires,
                        Ends = props.Ends,  
                        Severity = DetermineAlertSeverity(props.Event)
                    };

                    alerts.Add(alert);
                    Console.WriteLine($"Active alert: {alert.Type} (Severity: {alert.Severity})");
                }

                return alerts;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Alerts API error: {ex.Message}");
                return new List<WeatherAlert>();
            }
        }

        private AlertSeverity DetermineAlertSeverity(string eventType)
        {
            var lowerEvent = eventType.ToLower();

            // Extreme alerts
            if (lowerEvent.Contains("blizzard warning") ||
                lowerEvent.Contains("ice storm warning"))
            {
                return AlertSeverity.Extreme;
            }

            // Warnings
            if (lowerEvent.Contains("winter storm warning") ||
                lowerEvent.Contains("snow squall warning") ||
                lowerEvent.Contains("extreme cold warning"))
            {
                return AlertSeverity.Warning;
            }

            // Watches
            if (lowerEvent.Contains("winter storm watch") ||
                lowerEvent.Contains("blizzard watch"))
            {
                return AlertSeverity.Watch;
            }

            // Advisories
            if (lowerEvent.Contains("winter weather advisory") ||
                lowerEvent.Contains("wind chill advisory") ||
                lowerEvent.Contains("freezing rain advisory") ||
                lowerEvent.Contains("snow advisory"))
            {
                return AlertSeverity.Advisory;
            }

            return AlertSeverity.None;
        }

    }
}