﻿using Newtonsoft.Json.Linq;
using StableUI.Core;
using StableUI.Backends;
using StableUI.Utils;
using StableUI.Accounts;
using System.Runtime.ConstrainedExecution;

namespace StableUI.WebAPI;

/// <summary>Internal helper for all the basic API routes.</summary>
public static class BasicAPIFeatures
{
    /// <summary>Called by <see cref="Program"/> to register the core API calls.</summary>
    public static void Register()
    {
        API.RegisterAPICall(GetNewSession);
        API.RegisterAPICall(GenerateText2Image);
    }

#pragma warning disable CS1998 // "CS1998 Async method lacks 'await' operators and will run synchronously"

    /// <summary>API Route to create a new session automatically.</summary>
    public static async Task<JObject> GetNewSession(HttpContext context)
    {
        return new JObject() { ["session_id"] = Program.Sessions.CreateAdminSession(context.Connection.RemoteIpAddress?.ToString() ?? "unknown").ID };
    }

    /// <summary>API route to generate an image.</summary>
    public static async Task<JObject> GenerateText2Image(string prompt, string negative_prompt = "", int images = 1, long seed = -1, int steps = 20, double cfg_scale = 7, int width = 512, int height = 512)
    {
        List<Image> allOutputs = new();
        if (seed == -1)
        {
            seed = Random.Shared.Next(int.MaxValue);
        }
        JObject errorOut = null;
        List<Task> tasks = new();
        int max_degrees = 4; // TODO: Configure max degrees parallel based on user limit / global limit / backend count
        for (int i = 0; i < images; i++)
        {
            tasks.RemoveAll(t => t.IsCompleted);
            if (tasks.Count > max_degrees)
            {
                await Task.WhenAny(tasks);
            }
            if (Volatile.Read(ref errorOut) is not null)
            {
                return errorOut;
            }
            int index = i;
            tasks.Add(Task.Run(async () =>
            {
                T2IBackendAccess backend;
                try
                {
                    backend = Program.Backends.GetNextT2IBackend(TimeSpan.FromMinutes(2)); // TODO: Max timespan configurable
                }
                catch (InvalidOperationException ex)
                {
                    Volatile.Write(ref errorOut, new JObject() { ["error"] = $"Invalid operation: {ex.Message}" });
                    return;
                }
                catch (TimeoutException)
                {
                    Volatile.Write(ref errorOut, new JObject() { ["error"] = "Timeout! All backends are occupied with other tasks." });
                    return;
                }
                if (Volatile.Read(ref errorOut) is not null)
                {
                    return;
                }
                using (backend)
                {
                    Image[] outputs = await backend.Backend.Generate(prompt, negative_prompt, seed + index, steps, width, height, cfg_scale);
                    allOutputs.AddRange(outputs);
                }
            }));
        }
        await Task.WhenAll(tasks);
        errorOut = Volatile.Read(ref errorOut);
        if (errorOut is not null)
        {
            return errorOut;
        }
        return new JObject() { ["images"] = JToken.FromObject(allOutputs.Select(i => i.AsBase64).ToList()) };
    }
}