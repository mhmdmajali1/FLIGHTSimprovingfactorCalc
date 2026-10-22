// CalculateQbAllAircraft.cs
// Standard .NET console application; no NuGet packages required.
// Input: H145-LUH-SOF_dataSS.json
// Output: Qb_results.csv

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const string InputFile = "H145-LUH-SOF_data.json";
    private const string OutputFile = "Qb_results.csv";
    // Use "" to select the first profile matching each Part.
    private const string ProfileName = "CUSTOM PROFILE";

    private static int Main()
    {
        if (!File.Exists(InputFile))
        {
            Console.Error.WriteLine($"Cannot find input file: {InputFile}");
            Console.Error.WriteLine("Put the JSON file in the same folder as the program's working directory.");
            return 1;
        }

        Console.WriteLine($"Reading JSON file: {InputFile}");
        Console.WriteLine("The file is large; loading it may take time and several GB of RAM.");

        try
        {
            using FileStream input = File.OpenRead(InputFile);
            using JsonDocument document = JsonDocument.Parse(input);
            JsonElement root = document.RootElement;
            JsonElement configurations = GetArray(root, "Configurations");
            JsonElement aircrafts = GetArray(root, "Aircraft");
            int rowsWritten = 0;

            using (var writer = new StreamWriter(OutputFile, false, new UTF8Encoding(false)))
            {
                writer.WriteLine("TailNumber,TestNumber,FlightNumber,FlightStarted,ConfigurationUsed,TestingTaskUsed,Part,ProfileName,Qb,MeasurementTerms,MissingMeasurements");
                int aircraftIndex = 0;

                foreach (JsonElement aircraft in EnumerateArray(aircrafts))
                {
                    aircraftIndex++;
                    string tail = GetString(aircraft, "TailNumber");
                    JsonElement tests = GetArray(aircraft, "Tests");

                    foreach (JsonElement test in EnumerateArray(tests))
                    {
                        string testNumber = GetNumberText(test, "TestNumber");
                        JsonElement flights = GetArray(test, "Flights");

                        foreach (JsonElement flight in EnumerateArray(flights))
                        {
                            string flightNumber = GetNumberText(flight, "FlightNumber");
                            string flightStarted = GetString(flight, "Started");
                            string configId = GetString(flight, "ConfigurationUsed");
                            string taskId = GetNumberText(flight, "TestingTaskUsed");

                            JsonElement config = FindConfiguration(configurations, configId);
                            if (config.ValueKind == JsonValueKind.Undefined)
                            {
                                Console.Error.WriteLine($"Warning: no configuration '{configId}' for tail {tail}, test {testNumber}, flight {flightNumber}. Skipping.");
                                continue;
                            }

                            JsonElement task = FindTask(config, taskId);
                            if (task.ValueKind == JsonValueKind.Undefined)
                            {
                                Console.Error.WriteLine($"Warning: no matching testing task for tail {tail}, test {testNumber}, flight {flightNumber}. Skipping.");
                                continue;
                            }

                            JsonElement profiles = GetArray(task, "SolveProfiles");
                            if (!EnumerateArray(profiles).Any())
                            {
                                Console.Error.WriteLine($"Warning: no solve profiles for tail {tail}, test {testNumber}, flight {flightNumber}. Skipping.");
                                continue;
                            }

                            JsonElement trackData = GetArray(flight, "TrackData");
                            JsonElement balanceData = GetArray(flight, "BalanceData");
                            var parts = new List<string>();
                            AddParts(parts, trackData);
                            AddParts(parts, balanceData);

                            foreach (JsonElement profileItem in EnumerateArray(profiles))
                            {
                                string p = GetString(profileItem, "Part");
                                if (!string.IsNullOrWhiteSpace(p) && !parts.Contains(p, StringComparer.Ordinal))
                                    parts.Add(p);
                            }

                            foreach (string part in parts)
                            {
                                JsonElement profile = FindProfile(profiles, part, ProfileName);
                                if (profile.ValueKind == JsonValueKind.Undefined)
                                {
                                    Console.Error.WriteLine($"Warning: no matching profile for part {part}, tail {tail}, test {testNumber}, flight {flightNumber}.");
                                    continue;
                                }

                                JsonElement conditions = GetArray(profile, "TestConditions");
                                double sumSquares = 0.0;
                                int terms = 0;
                                int missing = 0;

                                foreach (JsonElement condition in EnumerateArray(conditions))
                                {
                                    string conditionName = GetString(condition, "TestCondition");
                                    JsonElement trackConfig = GetObject(condition, "Track");
                                    bool trackEnabled = GetBool(trackConfig, "IsEnabledForSolve", true);
                                    double trackLimit = GetNumber(trackConfig, "Limit", double.NaN);

                                    if (trackEnabled && double.IsFinite(trackLimit) && trackLimit > 0)
                                    {
                                        JsonElement measurement = FindTrack(trackData, part, conditionName);
                                        double ratio;
                                        if (measurement.ValueKind == JsonValueKind.Undefined ||
                                            !TryGetNumber(measurement, "TrackSplit", out double measuredTrack))
                                        {
                                            ratio = 10.0;
                                            missing++;
                                        }
                                        else ratio = measuredTrack / trackLimit;

                                        sumSquares += ratio * ratio;
                                        terms++;
                                    }

                                    JsonElement axes = GetArray(condition, "BalanceAxes");
                                    foreach (JsonElement axisConfig in EnumerateArray(axes))
                                    {
                                        if (!GetBool(axisConfig, "IsEnabledForSolve", true))
                                            continue;

                                        string axisName = GetString(axisConfig, "Axis");
                                        double axisLimit = GetNumber(axisConfig, "Limit", double.NaN);
                                        if (!double.IsFinite(axisLimit) || axisLimit <= 0)
                                            continue;

                                        JsonElement measurement = FindBalance(balanceData, part, conditionName, axisName);
                                        double ratio;
                                        if (measurement.ValueKind == JsonValueKind.Undefined ||
                                            !TryGetNumber(measurement, "Amplitude", out double measuredAmplitude))
                                        {
                                            ratio = 10.0;
                                            missing++;
                                        }
                                        else ratio = measuredAmplitude / axisLimit;

                                        sumSquares += ratio * ratio;
                                        terms++;
                                    }
                                }

                                double qb = terms > 0 ? sumSquares / terms : double.NaN;
                                string profileName = GetString(profile, "ProfileName");

                                writer.WriteLine(string.Join(",",
                                    Csv(tail), Csv(testNumber), Csv(flightNumber), Csv(flightStarted),
                                    Csv(configId), Csv(taskId), Csv(part), Csv(profileName),
                                    double.IsFinite(qb) ? qb.ToString("G12", CultureInfo.InvariantCulture) : "",
                                    terms.ToString(CultureInfo.InvariantCulture),
                                    missing.ToString(CultureInfo.InvariantCulture)));
                                rowsWritten++;
                            }
                        }
                    }

                    if (aircraftIndex % 10 == 0)
                        Console.WriteLine($"Processed {aircraftIndex} aircraft...");
                }
            }

            Console.WriteLine($"Finished. Wrote {rowsWritten} rows to {OutputFile}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("The calculation failed:");
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static IEnumerable<JsonElement> EnumerateArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) yield break;
        foreach (JsonElement item in element.EnumerateArray()) yield return item;
    }

    private static JsonElement GetArray(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Array) return value;
        return default;
    }

    private static JsonElement GetObject(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement value) &&
            value.ValueKind == JsonValueKind.Object) return value;
        return default;
    }

    private static string GetString(JsonElement element, string name, string fallback = "")
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return fallback;
        if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? fallback;
        return value.ToString();
    }

    private static string GetNumberText(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        return value.ToString();
    }

    private static double GetNumber(JsonElement element, string name, double fallback) =>
        TryGetNumber(element, name, out double number) ? number : fallback;

    private static bool TryGetNumber(JsonElement element, string name, out double number)
    {
        number = double.NaN;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number) return false;
        return value.TryGetDouble(out number) && double.IsFinite(number);
    }

    private static bool GetBool(JsonElement element, string name, bool fallback)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
        }
        return fallback;
    }

    private static JsonElement FindConfiguration(JsonElement configurations, string configId)
    {
        foreach (JsonElement config in EnumerateArray(configurations))
            if (string.Equals(GetString(config, "UniqueIdentifier"), configId, StringComparison.Ordinal))
                return config;
        return default;
    }

    private static JsonElement FindTask(JsonElement config, string taskId)
    {
        JsonElement tasks = GetArray(config, "TestingTasks");
        if (!string.IsNullOrWhiteSpace(taskId))
        {
            foreach (JsonElement task in EnumerateArray(tasks))
                if (string.Equals(GetNumberText(task, "Id"), taskId, StringComparison.Ordinal))
                    return task;
            return default;
        }
        return EnumerateArray(tasks).FirstOrDefault();
    }

    private static JsonElement FindProfile(JsonElement profiles, string part, string requestedName)
    {
        foreach (JsonElement profile in EnumerateArray(profiles))
        {
            if (!string.Equals(GetString(profile, "Part"), part, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(requestedName) ||
                string.Equals(GetString(profile, "ProfileName"), requestedName, StringComparison.Ordinal))
                return profile;
        }
        return default;
    }

    private static void AddParts(List<string> parts, JsonElement measurements)
    {
        foreach (JsonElement item in EnumerateArray(measurements))
        {
            string part = GetString(item, "Part");
            if (!string.IsNullOrWhiteSpace(part) && !parts.Contains(part, StringComparer.Ordinal))
                parts.Add(part);
        }
    }

    private static JsonElement FindTrack(JsonElement trackData, string part, string condition)
    {
        foreach (JsonElement item in EnumerateArray(trackData))
            if (GetString(item, "Part") == part && GetString(item, "TestCondition") == condition)
                return item;
        return default;
    }

    private static JsonElement FindBalance(JsonElement balanceData, string part, string condition, string axis)
    {
        foreach (JsonElement item in EnumerateArray(balanceData))
            if (GetString(item, "Part") == part &&
                GetString(item, "TestCondition") == condition &&
                GetString(item, "Axis") == axis) return item;
        return default;
    }

    private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
}

