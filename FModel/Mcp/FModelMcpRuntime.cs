using System;
using System.IO;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CUE4Parse.FileProvider.Objects;
using FModel.Services;
using FModel.Settings;
using FModel.Framework;
using FModel.ViewModels;
using Newtonsoft.Json;

namespace FModel.Mcp;

/// <summary>Owns the single mutable FModel provider used by stdio MCP requests.</summary>
public sealed class FModelMcpRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly ConcurrentDictionary<string, string[]> _assetTypes = new(StringComparer.OrdinalIgnoreCase);
    private bool _settingsLoaded;
    private ApplicationViewModel? _application;
    public bool IsInitialized => _application != null;

    public async Task<T> RunExclusiveAsync<T>(Func<CUE4ParseViewModel, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            return await operation(_application!.CUE4Parse, cancellationToken);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<T> RunSettingsExclusiveAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            LoadSettings();
            return await operation(cancellationToken);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_application != null) return;
        await _initialization.WaitAsync(cancellationToken);
        try
        {
            if (_application != null) return;
            InitializeSettings();

            if (Application.Current == null)
                throw new InvalidOperationException("FModel MCP requires a Windows application dispatcher.");

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                _application = ApplicationService.ApplicationView;
                var cue = _application.CUE4Parse;
                await Task.WhenAll(ApplicationViewModel.InitOodle(), ApplicationViewModel.InitZlib());
                await cue.Initialize();
                await _application.AesManager.InitAes();
                await _application.UpdateProvider(true);
                await Task.WhenAll(cue.InitMappings(), ApplicationViewModel.InitDetex(), cue.VerifyConsoleVariables(), cue.VerifyOnDemandArchives());
            }).Task.Unwrap();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FModel MCP provider initialization failed: {exception.GetType().FullName}");
            throw;
        }
        finally
        {
            _initialization.Release();
        }
    }

    private void InitializeSettings()
    {
        LoadSettings();
        if (UserSettings.Default.CurrentDir != null) return;
        if (!UserSettings.Default.PerDirectory.TryGetValue(UserSettings.Default.GameDirectory, out var directorySettings))
            throw new InvalidOperationException("FModel MCP requires a game-directory configuration. Call fmodel_configure_game or configure and open the game once in the GUI.");
        UserSettings.Default.CurrentDir = directorySettings;
    }

    private void LoadSettings()
    {
        if (_settingsLoaded) return;
        try
        {
            UserSettings.Default = JsonConvert.DeserializeObject<UserSettings>(
                File.ReadAllText(UserSettings.FilePath), JsonNetSerializer.SerializerSettings) ?? new UserSettings();
        }
        catch
        {
            UserSettings.Default = new UserSettings();
        }

        if (string.IsNullOrWhiteSpace(UserSettings.Default.OutputDirectory))
            UserSettings.Default.OutputDirectory = Path.Combine(AppContext.BaseDirectory, "Output");
        Directory.CreateDirectory(UserSettings.Default.OutputDirectory);
        UserSettings.Default.RawDataDirectory = DefaultDirectory(UserSettings.Default.RawDataDirectory, "Exports");
        UserSettings.Default.PropertiesDirectory = DefaultDirectory(UserSettings.Default.PropertiesDirectory, "Exports");
        UserSettings.Default.TextureDirectory = DefaultDirectory(UserSettings.Default.TextureDirectory, "Exports");
        UserSettings.Default.AudioDirectory = DefaultDirectory(UserSettings.Default.AudioDirectory, "Exports");
        UserSettings.Default.CodeDirectory = DefaultDirectory(UserSettings.Default.CodeDirectory, "Exports");
        UserSettings.Default.ModelDirectory = DefaultDirectory(UserSettings.Default.ModelDirectory, "Exports");
        Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FModel"));
        Directory.CreateDirectory(Path.Combine(UserSettings.Default.OutputDirectory, "Logs"));
        _settingsLoaded = true;
    }

    private static string DefaultDirectory(string? value, string name)
    {
        var directory = string.IsNullOrWhiteSpace(value) ? Path.Combine(UserSettings.Default.OutputDirectory, name) : value;
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static GameFile GetFile(CUE4ParseViewModel cue, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !cue.Provider.Files.TryGetValue(path.Replace('\\', '/'), out var entry))
            throw new FileNotFoundException("The requested FModel asset was not found.", path);
        return entry;
    }

    public string[] GetAssetTypes(CUE4ParseViewModel cue, GameFile entry)
    {
        if (entry.Extension is not ("uasset" or "umap")) return [];
        return _assetTypes.GetOrAdd(entry.Path, _ => cue.Provider.LoadPackage(entry).GetExports()
            .Select(export => export.GetType().Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _initialization.Dispose();
        _operations.Dispose();
        return ValueTask.CompletedTask;
    }
}
