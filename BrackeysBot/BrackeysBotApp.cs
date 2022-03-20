﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BrackeysBot.API;
using BrackeysBot.API.Plugins;
using BrackeysBot.Plugins;
using Microsoft.Extensions.Hosting;
using NLog;

namespace BrackeysBot;

/// <summary>
///     Represents a bot application instance.
/// </summary>
internal sealed class BrackeysBotApp : BackgroundService, IBot
{
    private readonly List<Assembly> _libraries = new();

    static BrackeysBotApp()
    {
        var apiAssembly = Assembly.GetAssembly(typeof(IBot))!;
        var assembly = Assembly.GetAssembly(typeof(BrackeysBotApp))!;

        ApiVersion = apiAssembly.GetName().Version!.ToString(3);
        Version = assembly.GetName().Version!.ToString(3);
    }

    /// <summary>
    ///     Gets the version of the API in use.
    /// </summary>
    /// <value>The API version.</value>
    public static string ApiVersion { get; }

    /// <summary>
    ///     Gets the <c>libraries</c> directory for this bot.
    /// </summary>
    /// <value>The <c>libraries</c> directory.</value>
    public DirectoryInfo LibrariesDirectory { get; } = new("libraries");

    /// <inheritdoc />
    public ILogger Logger { get; } = LogManager.GetLogger("BrackeysBot");

    /// <inheritdoc />
    public IPluginManager PluginManager { get; } = new SimplePluginManager();

    /// <inheritdoc />
    public static string Version { get; }

    /// <summary>
    ///     Disables all currently-loaded and currently-enabled plugins.
    /// </summary>
    public void DisablePlugins()
    {
        foreach (IPlugin plugin in PluginManager.EnabledPlugins)
            PluginManager.DisablePlugin(plugin);
    }

    /// <summary>
    ///     Enables all currently-loaded plugins.
    /// </summary>
    public void EnablePlugins()
    {
        foreach (IPlugin plugin in PluginManager.LoadedPlugins)
            PluginManager.EnablePlugin(plugin);
    }

    /// <summary>
    ///     Loads third party dependencies as found <see cref="LibrariesDirectory" />.
    /// </summary>
    public void LoadLibraries()
    {
        if (!LibrariesDirectory.Exists) return;

        foreach (FileInfo file in LibrariesDirectory.EnumerateFiles("*.dll"))
        {
            try
            {
                Assembly assembly = Assembly.LoadFrom(file.FullName);
                if (!_libraries.Contains(assembly))
                    _libraries.Add(assembly);
                Logger.Debug($"Loaded {assembly.GetName()}");
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, $"Could not load library '{file.Name}' - SOME PLUGINS MAY NOT WORK");
            }
        }
    }

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.Info($"Starting Brackeys Bot version {Version} with API version {ApiVersion}");

        LoadLibraries();
        int libraryCount = _libraries.Count;
        Logger.Info($"Loaded {libraryCount} {(libraryCount == 1 ? "library" : "libraries")}");

        PluginManager.LoadPlugins();
        int loadedPluginCount = PluginManager.LoadedPlugins.Count;
        Logger.Info($"Loaded {loadedPluginCount} {(loadedPluginCount == 1 ? "plugin" : "plugins")}");

        EnablePlugins();
        int enabledPluginCount = PluginManager.EnabledPlugins.Count;
        Logger.Info($"Enabled {enabledPluginCount} {(enabledPluginCount == 1 ? "plugin" : "plugins")}");

        return Task.CompletedTask;
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        DisablePlugins();

        foreach (IPlugin plugin in PluginManager.LoadedPlugins)
            PluginManager.UnloadPlugin(plugin);

        return base.StopAsync(cancellationToken);
    }
}
