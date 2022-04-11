using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using BrackeysBot.API.Attributes;
using BrackeysBot.API.Exceptions;
using BrackeysBot.API.Plugins;
using BrackeysBot.ArgumentConverters;
using BrackeysBot.Commands;
using BrackeysBot.Configuration;
using BrackeysBot.Logging;
using BrackeysBot.Resources;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Converters;
using DSharpPlus.CommandsNext.Exceptions;
using DSharpPlus.SlashCommands;
using Emzi0767.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using ILogger = NLog.ILogger;

namespace BrackeysBot.Plugins;

/// <summary>
///     Represents a plugin manager which can load .NET assemblies as plugins.
/// </summary>
internal sealed class SimplePluginManager : IPluginManager
{
    private readonly Dictionary<IPlugin, List<string>> _commands = new();
    private readonly Dictionary<IPlugin, bool> _loadedPlugins = new();
    private readonly List<IPlugin> _tokenlessPlugins = new();
    private readonly Stack<string> _pluginLoadStack = new();

    /// <summary>
    ///     Gets the plugin directory.
    /// </summary>
    /// <value>The plugin directory.</value>
    public DirectoryInfo PluginDirectory { get; } = new("plugins");

    /// <inheritdoc />
    public event AsyncEventHandler<IPluginManager, PluginLoadEventArgs>? PluginLoaded;

    /// <inheritdoc />
    public IReadOnlyList<IPlugin> EnabledPlugins => _loadedPlugins.Where(p => p.Value).Select(p => p.Key).ToArray();

    /// <inheritdoc />
    public IReadOnlyList<IPlugin> LoadedPlugins => _loadedPlugins.Keys.ToArray();

    /// <inheritdoc />
    public ILogger Logger { get; } = LogManager.GetLogger(nameof(SimplePluginManager));

    /// <inheritdoc />
    public void DisablePlugin(IPlugin plugin)
    {
        if (plugin is null) throw new ArgumentNullException(nameof(plugin));
        if (!_loadedPlugins.ContainsKey(plugin)) throw new PluginNotLoadedException(plugin);
        if (!_loadedPlugins[plugin]) return;
        if (plugin is not MonoPlugin monoPlugin) return;

        foreach (IHostedService hostedService in plugin.ServiceProvider.GetServices<IHostedService>())
        {
            try
            {
                hostedService.StopAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, string.Format(LoggerMessages.ExceptionWhenStoppingService, hostedService.GetType()));
            }
        }

        try
        {
            monoPlugin.OnDisable().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, string.Format(LoggerMessages.ExceptionWhenDisablingPlugin, plugin.PluginInfo.Name));
        }

        _loadedPlugins[plugin] = false;

        monoPlugin.EnableTime = null;

        if (!_tokenlessPlugins.Contains(plugin))
            monoPlugin.DiscordClient.DisconnectAsync();

        Logger.Info(string.Format(LoggerMessages.DisabledPlugin, plugin.PluginInfo.Name, plugin.PluginInfo.Version));
    }

    /// <inheritdoc />
    public void EnablePlugin(IPlugin plugin)
    {
        if (plugin is null) throw new ArgumentNullException(nameof(plugin));
        if (!_loadedPlugins.ContainsKey(plugin)) throw new PluginNotLoadedException(plugin);
        if (_loadedPlugins[plugin]) return;
        if (plugin is not MonoPlugin monoPlugin) return;

        monoPlugin.EnableTime = DateTimeOffset.UtcNow;

        foreach (IHostedService hostedService in plugin.ServiceProvider.GetServices<IHostedService>())
        {
            try
            {
                hostedService.StartAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, string.Format(LoggerMessages.ExceptionWhenStartingService, hostedService.GetType()));
            }
        }

        try
        {
            monoPlugin.OnEnable().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, string.Format(LoggerMessages.ExceptionWhenEnablingPlugin, plugin.PluginInfo.Name));
            return;
        }

        if (!_tokenlessPlugins.Contains(plugin))
            monoPlugin.DiscordClient.ConnectAsync();

        _loadedPlugins[plugin] = true;
        Logger.Info(string.Format(LoggerMessages.EnabledPlugin, plugin.PluginInfo.Name, plugin.PluginInfo.Version));
    }

    /// <inheritdoc />
    public T? GetPlugin<T>() where T : IPlugin
    {
        foreach ((IPlugin? plugin, bool _) in _loadedPlugins)
        {
            if (plugin is T actual)
                return actual;
        }

        return default;
    }

    /// <inheritdoc />
    public IPlugin? GetPlugin(string name)
    {
        foreach ((IPlugin? plugin, bool _) in _loadedPlugins)
        {
            if (string.Equals(name, plugin.PluginInfo.Name))
                return plugin;
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsPluginEnabled(IPlugin plugin)
    {
        return _loadedPlugins.TryGetValue(plugin, out bool enabled) && enabled;
    }

    /// <inheritdoc />
    public bool IsPluginLoaded(IPlugin plugin)
    {
        return _loadedPlugins.ContainsKey(plugin);
    }

    /// <inheritdoc />
    public IPlugin LoadPlugin(string name)
    {
        if (_pluginLoadStack.Contains(name))
            throw new CircularPluginDependencyException(name);

        if (TryGetPlugin(name, out IPlugin? loadedPlugin))
        {
            Logger.Debug(string.Format(LoggerMessages.PluginAlreadyLoaded, name));
            return loadedPlugin;
        }

        _pluginLoadStack.Push(name);

        var file = new FileInfo(Path.Combine(PluginDirectory.FullName, $"{name}.dll"));
        if (!file.Exists) throw new PluginNotFoundException(name);

        var context = new AssemblyLoadContext(name, true);
        using FileStream stream = file.Open(FileMode.Open, FileAccess.Read);
        Assembly assembly = context.LoadFromStream(stream);

        Type pluginType = GetPluginType(name, assembly, out PluginAttribute? pluginAttribute);
        name = pluginAttribute.Name;

        if (_loadedPlugins.Any(p => string.Equals(name, p.Key.PluginInfo.Name)))
            throw new InvalidPluginException(name, string.Format(ExceptionMessages.DuplicatePluginName, name));

        var informationalVersionAttribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        string? version = informationalVersionAttribute?.InformationalVersion ?? assembly.GetName().Version?.ToString();
        if (version is null)
        {
            version ??= "0.0.0";
            Logger.Warn(string.Format(LoggerMessages.PluginVersionNotDetected, name));
        }

        var descriptionAttribute = pluginType.GetCustomAttribute<PluginDescriptionAttribute>();
        string? description = descriptionAttribute?.Description;

        IPlugin[] dependencies = EnumeratePluginDependencies(pluginType).ToArray();

        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (dependencies.Any(d => d is null))
            return null!;

        PluginInfo.PluginAuthorInfo? authorInfo = GetPluginAuthorInfo(pluginType);
        var pluginInfo = new PluginInfo(name, version, description, authorInfo, dependencies.Select(d => d.PluginInfo).ToArray());
        if (Activator.CreateInstance(pluginType) is not MonoPlugin instance)
            throw new InvalidPluginException(name, ExceptionMessages.NoDerivationOfPluginClass);

        instance.Dependencies = dependencies.ToArray(); // defensive copy
        instance.LoadContext = context;
        instance.PluginInfo = pluginInfo;
        instance.PluginManager = this;
        instance.Logger = LogManager.GetLogger(pluginInfo.Name);

        UpdatePluginDependants(instance);
        SetupPluginDataDirectory(pluginInfo, instance);
        JsonFileConfiguration configuration = SetupPluginConfiguration(instance);
        SetupPluginServices(instance, pluginInfo, pluginType);
        (CommandsNextExtension? commandsNext, SlashCommandsExtension? slashCommands) = SetupPluginCommands(instance);

        instance.OnLoad().GetAwaiter().GetResult();

        if (!_tokenlessPlugins.Contains(instance))
        {
            if (commandsNext is not null)
            {
                commandsNext.UnregisterConverter<TimeSpanConverter>();
                commandsNext.RegisterConverter(new DateOnlyArgumentConverter());
                commandsNext.RegisterConverter(new TimeOnlyArgumentConverter());
                commandsNext.RegisterConverter(new TimeSpanArgumentConverter());

                string prefix = configuration.Get<string>("discord.prefix") ?? "[]";
                string[] commandNames = commandsNext.RegisteredCommands.Keys.OrderBy(c => c).Select(c => prefix + c).ToArray();
                Logger.Info(string.Format(LoggerMessages.PluginRegisteredCommands, pluginInfo.Name, commandNames.Length,
                    string.Join(", ", commandNames)));

                CheckDuplicateCommands(instance, commandsNext.RegisteredCommands);
                _commands.Add(instance, commandNames.ToList());
            }

            RegisterCommandEvents(instance, commandsNext, slashCommands);
        }

        Logger.Info(string.Format(LoggerMessages.LoadedPlugin, pluginInfo.Name, pluginInfo.Version));

        _pluginLoadStack.Pop();
        _loadedPlugins.Add(instance, false);

        PluginLoaded?.Invoke(this, new PluginLoadEventArgs(instance));
        return instance;
    }

    /// <inheritdoc />
    public IReadOnlyList<IPlugin> LoadPlugins()
    {
        try
        {
            PluginDirectory.Create();
        }
        catch (IOException exception)
        {
            Logger.Warn(exception, string.Format(LoggerMessages.PluginDirectoryCantBeCreated, PluginDirectory.FullName));
            return ArraySegment<IPlugin>.Empty;
        }

        var plugins = new List<IPlugin>();

        foreach (FileInfo file in PluginDirectory.EnumerateFiles("*.dll"))
        {
            string pluginName = Path.GetFileNameWithoutExtension(file.Name);

            IPlugin? plugin = null;
            try
            {
                plugin = LoadPlugin(pluginName);

                // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
                if (plugin is not null)
                    plugins.Add(plugin);
            }
            catch (Exception exception)
            {
                if (plugin is MonoPlugin monoPlugin && !_tokenlessPlugins.Contains(plugin))
                {
                    monoPlugin.DiscordClient.Dispose();
                    monoPlugin.DiscordClient = null!;
                }

                plugin?.Dispose();
                Logger.Error(exception, string.Format(LoggerMessages.ExceptionWhenLoadingPlugin, pluginName));
            }
        }

        return plugins.AsReadOnly();
    }

    /// <inheritdoc />
    public bool TryGetPlugin(string name, [NotNullWhen(true)] out IPlugin? plugin)
    {
        plugin = _loadedPlugins.Keys.FirstOrDefault(p => string.Equals(name, p.PluginInfo.Name));
        return plugin is not null;
    }

    /// <inheritdoc />
    public void UnloadPlugin(IPlugin plugin)
    {
        if (!IsPluginLoaded(plugin)) return;
        if (plugin is not MonoPlugin monoPlugin) return;

        DisablePlugin(plugin);

        foreach (IPlugin dependant in plugin.Dependants)
            UnloadPlugin(dependant);

        try
        {
            monoPlugin.OnUnload().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, string.Format(LoggerMessages.ExceptionWhenUnloadingPlugin, plugin.PluginInfo.Name));
        }

        if (!_tokenlessPlugins.Contains(plugin))
        {
            monoPlugin.DiscordClient.Dispose();
            monoPlugin.DiscordClient = null!;
        }

        plugin.Dispose();
        _tokenlessPlugins.Remove(plugin);

        monoPlugin.LoadContext.Unload();
        _commands.Remove(plugin);
        _loadedPlugins.Remove(plugin);

        Logger.Info(string.Format(LoggerMessages.UnloadedPlugin, plugin.PluginInfo.Name, plugin.PluginInfo.Version));
    }

    private static Type GetPluginType(string name, Assembly assembly, out PluginAttribute pluginAttribute)
    {
        Type[] pluginTypes = assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(MonoPlugin))).ToArray();
        if (pluginTypes.Length == 0)
            throw new InvalidPluginException(name, string.Format(ExceptionMessages.NoPluginClass, typeof(MonoPlugin)));

        if (pluginTypes.Length > 1)
        {
            throw new InvalidPluginException(name,
                string.Format(ExceptionMessages.MultiplePluginsInAssembly, typeof(MonoPlugin)));
        }

        Type pluginType = pluginTypes[0];
        pluginAttribute = pluginType.GetCustomAttribute<PluginAttribute>()!;
        if (pluginAttribute is null)
            throw new InvalidPluginException(name, string.Format(ExceptionMessages.NoPluginAttribute, typeof(PluginAttribute)));

        return pluginType;
    }

    private IEnumerable<IPlugin> EnumeratePluginDependencies(Type pluginType)
    {
        var pluginDependenciesAttribute = pluginType.GetCustomAttribute<PluginDependenciesAttribute>();
        if (pluginDependenciesAttribute?.Dependencies is not {Length: > 0} requestedDependencies)
            yield break;

        foreach (string requestedDependency in requestedDependencies)
        {
            IPlugin? dependency;

            try
            {
                dependency = LoadPlugin(requestedDependency);
            }
            catch (Exception exception)
            {
                Logger.Error(exception, string.Format(LoggerMessages.CouldNotLoadDependency, requestedDependency));
                dependency = null;
            }

            if (dependency is not null)
                yield return dependency;
        }
    }

    private static PluginInfo.PluginAuthorInfo? GetPluginAuthorInfo(Type pluginType)
    {
        var authorAttribute = pluginType.GetCustomAttribute<PluginAuthorAttribute>();
        if (authorAttribute is null)
            return null;

        return new PluginInfo.PluginAuthorInfo(authorAttribute.Name, authorAttribute.Email, authorAttribute.Url);
    }

    private static (CommandsNextExtension?, SlashCommandsExtension?) SetupPluginCommands(MonoPlugin plugin)
    {
        if (plugin.ServiceProvider.GetService<DiscordClient>() is not { } client) return (null, null);

        CommandsNextExtension commandsNext = client.UseCommandsNext(new CommandsNextConfiguration
        {
            Services = plugin.ServiceProvider,
            UseDefaultCommandHandler = true,
            StringPrefixes = new[] {plugin.Configuration.Get<string>("discord.prefix") ?? "[]"}
        });

        commandsNext.RegisterCommands<InfoCommand>();
        SlashCommandsExtension slashCommands = client.UseSlashCommands(new SlashCommandsConfiguration
        {
            Services = plugin.ServiceProvider
        });

        return (commandsNext, slashCommands);
    }

    private void SetupPluginDataDirectory(PluginInfo pluginInfo, MonoPlugin instance)
    {
        var dataDirectory = new DirectoryInfo(Path.Combine(PluginDirectory.FullName, pluginInfo.Name));
        dataDirectory.Create();
        instance.DataDirectory = dataDirectory;
    }

    private static JsonFileConfiguration SetupPluginConfiguration(MonoPlugin instance)
    {
        var jsonFileConfiguration = new JsonFileConfiguration();
        string configFilePath = Path.Combine(instance.DataDirectory.FullName, "config.json");
        jsonFileConfiguration.ConfigurationFile = new FileInfo(configFilePath);
        jsonFileConfiguration.SaveDefault();
        instance.Configuration = jsonFileConfiguration;
        return jsonFileConfiguration;
    }

    private void CheckDuplicateCommands(IPlugin plugin, IReadOnlyDictionary<string, Command> commands)
    {
        string pluginName = plugin.PluginInfo.Name;

        foreach ((IPlugin current, List<string> currentCommands) in _commands)
        {
            string currentPluginName = current.PluginInfo.Name;

            foreach ((string name, Command command) in commands)
            {
                if (!currentCommands.Contains(name)) continue;
                if (command.ExecutionChecks.Any(c => c is RequireMentionPrefixAttribute)) continue;
                Logger.Warn(string.Format(LoggerMessages.PluginCommandConflict, pluginName, command, currentPluginName));
            }
        }
    }

    private static void RegisterCommandEvents(IPlugin plugin, CommandsNextExtension? commandsNext,
        SlashCommandsExtension? slashCommands)
    {
        if (commandsNext is not null)
        {
            commandsNext.CommandErrored += (_, args) =>
            {
                CommandContext context = args.Context;
                if (context.Command is null) return Task.CompletedTask;
                if (args.Exception is ChecksFailedException) return Task.CompletedTask; // no need to log ChecksFailedException

                var name = $"{context.Prefix}{context.Command.Name}";
                plugin.Logger.Error(args.Exception, $"An exception was thrown when executing '{name}'");
                return Task.CompletedTask;
            };
        }

        if (slashCommands is not null)
        {
            slashCommands.ContextMenuErrored += (_, args) =>
            {
                ContextMenuContext context = args.Context;
                if (args.Exception is ChecksFailedException) return Task.CompletedTask; // no need to log ChecksFailedException

                string? name = context.Interaction.Data.Name;
                plugin.Logger.Error(args.Exception, $"An exception was thrown when executing context menu '{name}'");
                return Task.CompletedTask;
            };

            slashCommands.SlashCommandErrored += (_, args) =>
            {
                InteractionContext context = args.Context;
                if (args.Exception is ChecksFailedException) return Task.CompletedTask; // no need to log ChecksFailedException

                string? name = context.Interaction.Data.Name;
                plugin.Logger.Error(args.Exception, $"An exception was thrown when executing slash command '{name}'");
                return Task.CompletedTask;
            };
        }
    }

    private void SetupPluginServices(MonoPlugin instance, PluginInfo pluginInfo, Type pluginType)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddNLog();
        });

        serviceCollection.AddSingleton<IPluginManager>(this);
        serviceCollection.AddSingleton<IPlugin>(instance);
        serviceCollection.AddSingleton(instance.GetType(), instance);
        serviceCollection.AddSingleton(instance.Configuration);

        var token = instance.Configuration.Get<string>("discord.token");
        if (string.IsNullOrWhiteSpace(token))
        {
            Logger.Warn(string.Format(LoggerMessages.NoPluginToken, pluginInfo.Name));
            _tokenlessPlugins.Add(instance);
        }
        else
        {
            var intents = DiscordIntents.AllUnprivileged;
            var intentsAttribute = pluginType.GetCustomAttribute<PluginIntentsAttribute>();

            if (intentsAttribute is not null)
                intents = intentsAttribute.Intents;

            serviceCollection.AddSingleton(_ =>
            {
                var client = new DiscordClient(new DiscordConfiguration
                {
                    Intents = intents,
                    LoggerFactory = new PluginLoggerFactory(instance),
                    Token = token
                });

                instance.DiscordClient = client;
                return client;
            });
        }

        instance.ConfigureServices(serviceCollection);
        instance.ServiceProvider = serviceCollection.BuildServiceProvider();
    }

    private static void UpdatePluginDependants(IPlugin instance)
    {
        foreach (MonoPlugin dependency in instance.Dependencies.OfType<MonoPlugin>())
        {
            if (dependency.Dependants.Contains(instance)) continue;

            var dependants = new List<IPlugin>(dependency.Dependants) {instance};
            dependency.Dependants = dependants.ToArray();
        }
    }
}
