﻿using FreneticUtilities.FreneticDataSyntax;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using StableUI.Accounts;
using StableUI.Backends;
using StableUI.Core;
using StableUI.DataHolders;
using StableUI.Text2Image;
using StableUI.Utils;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using static StableUI.Core.Settings;

namespace StableUI.WebAPI;

/// <summary>Text-to-Image API routes</summary>
public static class T2IAPI
{
    public static void Register()
    {
        API.RegisterAPICall(ListBackendTypes);
        API.RegisterAPICall(ListBackends);
        API.RegisterAPICall(DeleteBackend);
        API.RegisterAPICall(EditBackend);
        API.RegisterAPICall(AddNewBackend);
        API.RegisterAPICall(GenerateText2Image);
        API.RegisterAPICall(GenerateText2ImageWS);
        API.RegisterAPICall(ListImages);
        API.RegisterAPICall(ListModels);
    }

#pragma warning disable CS1998 // "CS1998 Async method lacks 'await' operators and will run synchronously"

    /// <summary>API route to list currently available backend-types.</summary>
    public static async Task<JObject> ListBackendTypes()
    {
        return new() { ["list"] = JToken.FromObject(Program.Backends.BackendTypes.Values.Select(b => b.NetDescription).ToList()) };
    }

    /// <summary>Create a network object to represent a backend cleanly.</summary>
    public static JObject BackendToNet(BackendHandler.T2IBackendData backend)
    {
        return new JObject()
        {
            ["type"] = backend.Backend.HandlerTypeData.ID,
            ["valid"] = backend.Backend.IsValid,
            ["id"] = backend.ID,
            ["settings"] = JToken.FromObject(backend.Backend.InternalSettingsAccess.Save(true).ToSimple())
        };
    }

    /// <summary>API route to shutdown and delete a registered backend.</summary>
    public static async Task<JObject> DeleteBackend(int backend_id)
    {
        if (await Program.Backends.DeleteById(backend_id))
        {
            return new JObject() { ["result"] = "Deleted." };
        }
        return new JObject() { ["result"] = "Already didn't exist." };
    }

    /// <summary>API route to modify and re-init an already registered backend.</summary>
    public static async Task<JObject> EditBackend(int backend_id, JObject raw_inp)
    {
        if (!raw_inp.TryGetValue("settings", out JToken jval) || jval is not JObject settings)
        {
            return new() { ["error"] = "Missing settings." };
        }
        FDSSection parsed = FDSSection.FromSimple(settings.ToBasicObject());
        Console.WriteLine($"Will apply: {settings} as {parsed}");
        BackendHandler.T2IBackendData result = await Program.Backends.EditById(backend_id, parsed);
        if (result is null)
        {
            return new() { ["error"] = $"Invalid backend ID {backend_id}" };
        }
        return BackendToNet(result);
    }

    /// <summary>API route to list currently registered backends.</summary>
    public static async Task<JObject> ListBackends()
    {
        return new JObject() { ["list"] = JToken.FromObject(Program.Backends.T2IBackends.Values.OrderBy(d => d.ID).Select(BackendToNet).ToList()) };
    }

    /// <summary>API route to add a new backend.</summary>
    public static async Task<JObject> AddNewBackend(string type_id)
    {
        if (!Program.Backends.BackendTypes.TryGetValue(type_id, out BackendHandler.BackendType type))
        {
            return new() { ["error"] = $"Invalid backend type: {type_id}" };
        }
        BackendHandler.T2IBackendData data = Program.Backends.AddNewOfType(type);
        return BackendToNet(data);
    }

    /// <summary>API route to generate images with WebSocket updates.</summary>
    public static async Task<JObject> GenerateText2ImageWS(WebSocket socket, Session session, int images, T2IParams user_input)
    {
        await foreach ((string img, JObject err) in GenT2I_Internal(session, images, user_input))
        {
            if (img is not null)
            {
                await socket.SendJson(new JObject() { ["image"] = img }, TimeSpan.FromMinutes(1)); // TODO: Configurable timeout
            }
            if (err is not null)
            {
                await socket.SendJson(err, TimeSpan.FromMinutes(1));
                break;
            }
        }
        return null;
    }

    /// <summary>API route to generate images directly as HTTP.</summary>
    public static async Task<JObject> GenerateText2Image(Session session, int images, T2IParams user_input)
    {
        List<string> outputs = new();
        await foreach ((string img, JObject err) in GenT2I_Internal(session, images, user_input))
        {
            if (img is not null)
            {
                outputs.Add(img);
            }
            if (err is not null)
            {
                return err;
            }
        }
        return new JObject() { ["images"] = JToken.FromObject(outputs) };
    }

    /// <summary>Internal route for generating images.</summary>
    public static async IAsyncEnumerable<(string, JObject)> GenT2I_Internal(Session session, int images, T2IParams user_input)
    {
        ConcurrentQueue<string> allOutputs = new();
        user_input = user_input.Clone();
        if (user_input.Seed == -1)
        {
            user_input.Seed = Random.Shared.Next(int.MaxValue);
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
                yield return (null, errorOut);
                yield break;
            }
            while (allOutputs.TryDequeue(out string output))
            {
                yield return (output, null);
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
                    T2IParams thisParams = user_input.Clone();
                    thisParams.Seed += index;
                    Image[] outputs = await backend.Backend.Generate(thisParams);
                    foreach (Image image in outputs)
                    {
                        string url = session.SaveImage(image, thisParams);
                        if (url == "ERROR")
                        {
                            Volatile.Write(ref errorOut, new JObject() { ["error"] = $"Server failed to save images." });
                            return;
                        }
                        allOutputs.Enqueue(url);
                    }
                }
            }));
        }
        while (tasks.Any())
        {
            await Task.WhenAny(tasks);
            tasks.RemoveAll(t => t.IsCompleted);
            while (allOutputs.TryDequeue(out string output))
            {
                yield return (output, null);
            }
        }
        errorOut = Volatile.Read(ref errorOut);
        if (errorOut is not null)
        {
            yield return (null, errorOut);
            yield break;
        }
    }

    public static HashSet<string> ImageExtensions = new() { "png", "jpg" };

    /// <summary>API route to get a list of available history images.</summary>
    private static JObject GetListAPIInternal(Session session, string path, string root, HashSet<string> extensions, Func<string, bool> isAllowed, Func<string, string, JObject> valToObj)
    {
        (path, string consoleError, string userError) = WebServer.CheckFilePath(root, path);
        if (consoleError is not null)
        {
            Logs.Error(consoleError);
            return new JObject() { ["error"] = userError };
        }
        try
        {
            return new JObject()
            {
                ["folders"] = JToken.FromObject(Directory.EnumerateDirectories(path).Select(Path.GetFileName).Where(isAllowed).ToList()),
                ["files"] = JToken.FromObject(Directory.EnumerateFiles(path).Where(isAllowed).Where(f => extensions.Contains(f.AfterLast('.'))).Select(f => f.Replace('\\', '/')).Select(f => valToObj(f, f.AfterLast('/'))).ToList())
            };
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException || ex is DirectoryNotFoundException || ex is PathTooLongException)
            {
                return new JObject() { ["error"] = "404, path not found." };
            }
            else
            {
                return new JObject() { ["error"] = "Error reading file list." };
            }
        }
    }

    /// <summary>API route to get a list of available history images.</summary>
    public static async Task<JObject> ListImages(Session session, string path = "")
    {
        string root = $"{Environment.CurrentDirectory}/{Program.ServerSettings.OutputPath}/{session.User.UserID}";
        return GetListAPIInternal(session, path, root, ImageExtensions, f => true, (file, name) => new JObject() { ["src"] = name, ["batch_id"] = 0 });
    }

    public static HashSet<string> ModelExtensions = new() { "safetensors", "ckpt" };

    /// <summary>API route to get a list of available models.</summary>
    public static async Task<JObject> ListModels(Session session, string path = "")
    {
        string allowedStr = session.User.Restrictions.AllowedModels;
        if (path != "")
        {
            path += '/';
        }
        Regex allowed = allowedStr == ".*" ? null : new Regex(allowedStr, RegexOptions.Compiled | RegexOptions.IgnoreCase);
        List<T2IModel> matches = Program.T2IModels.Models.Values.Where(m => m.Name.StartsWith(path) && m.Name.Length > path.Length && (allowed is null || allowed.IsMatch(m.Name))).ToList();
        return new JObject()
        {
            ["folders"] = JToken.FromObject(matches.Where(m => m.Name[path.Length..].Contains('/')).Select(m => m.Name.BeforeLast('/').AfterLast('/')).Distinct().ToList()),
            ["files"] = JToken.FromObject(matches.Where(m => !m.Name[path.Length..].Contains('/')).Select(m => m.ToNetObject()).ToList())
        };
    }
}