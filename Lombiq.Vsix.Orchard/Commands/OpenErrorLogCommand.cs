using EnvDTE;
using Lombiq.Vsix.Orchard.Constants;
using Lombiq.Vsix.Orchard.Helpers;
using Lombiq.Vsix.Orchard.Models;
using Lombiq.Vsix.Orchard.Services.LogWatcher;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace Lombiq.Vsix.Orchard.Commands
{
    internal sealed class OpenErrorLogCommand : IAsyncDisposable
    {
        public const int CommandId = CommandIds.OpenErrorLogCommandId;
        public static readonly Guid CommandSet = PackageGuids.LombiqOrchardVisualStudioExtensionCommandSetGuid;

        private readonly AsyncPackage _package;
        private readonly ILogWatcherSettingsAccessor _logWatcherSettingsAccessor;
        private readonly IEnumerable<ILogFileWatcher> _logWatchers;
        private readonly IBlinkStickManager _blinkStickManager;
        private readonly object _settingsChangeLock = new object();

        private OleMenuCommand _openErrorLogCommand;
        private bool _hasSeenErrorLogUpdate;
        private bool _errorIndicatorStateChanged;
        private ILogFileStatus _latestUpdatedLogFileStatus;

        private OpenErrorLogCommand(
            AsyncPackage package,
            ILogWatcherSettingsAccessor logWatcherSettingsAccessor,
            IEnumerable<ILogFileWatcher> logWatchers,
            IBlinkStickManager blinkStickManager)
        {
            _package = package;
            _logWatcherSettingsAccessor = logWatcherSettingsAccessor;
            _logWatchers = logWatchers;
            _blinkStickManager = blinkStickManager;
        }

        public static OpenErrorLogCommand Instance { get; private set; }

        public static async Task CreateAsync(AsyncPackage package, ILogWatcherSettingsAccessor logWatcherSettingsAccessor)
        {
            Instance = Instance ?? new OpenErrorLogCommand(
                package,
                logWatcherSettingsAccessor,
                await package.GetServicesAsync<ILogFileWatcher>().ConfigureAwait(true),
                await package.GetServiceAsync<IBlinkStickManager>().ConfigureAwait(true));

            await Instance.InitalizeWatchersAsync().ConfigureAwait(true);
        }

        public async Task InitializeUIAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _openErrorLogCommand = new OleMenuCommand(OpenErrorLogCallback, new CommandID(CommandSet, CommandId));
            _openErrorLogCommand.BeforeQueryStatus += OpenErrorLogCommandBeforeQueryStatusCallback;
            _openErrorLogCommand.Enabled = false;

            (await _package.GetServiceAsync<IMenuCommandService>().ConfigureAwait(true)).AddCommand(_openErrorLogCommand);

            if ((await _logWatcherSettingsAccessor.GetSettingsAsync().ConfigureAwait(true)).LogWatcherEnabled) _openErrorLogCommand.Visible = true;
        }

        public async ValueTask DisposeAsync()
        {
            _blinkStickManager.Dispose();

            foreach (var watcher in _logWatchers)
            {
                watcher.LogUpdated -= LogFileUpdatedCallback;
                watcher.Dispose();
            }

            (await _logWatcherSettingsAccessor.GetSettingsAsync().ConfigureAwait(true)).SettingsUpdated -= LogWatcherSettingsUpdatedCallback;
        }

        private async Task InitalizeWatchersAsync()
        {
            _hasSeenErrorLogUpdate = true;
            _errorIndicatorStateChanged = true;

            foreach (var watcher in _logWatchers)
            {
                watcher.LogUpdated += LogFileUpdatedCallback;
            }

            var settings = await _logWatcherSettingsAccessor.GetSettingsAsync().ConfigureAwait(true);
            settings.SettingsUpdated += LogWatcherSettingsUpdatedCallback;

            if (settings.LogWatcherEnabled) StartLogFileWatching();
        }

        private void OpenErrorLogCommandBeforeQueryStatusCallback(object sender, EventArgs e) =>
            _ = UpdateOpenErrorLogCommandAccessibilityAndTextAsync();

        private void LogFileUpdatedCallback(object sender, LogChangedEventArgs context)
        {
            _hasSeenErrorLogUpdate = !context.LogFileStatus.HasContent;
            _latestUpdatedLogFileStatus = context.LogFileStatus;

            _ = UpdateOpenErrorLogCommandAccessibilityAndTextAsync(context.LogFileStatus);
        }

        private void OpenErrorLogCallback(object sender, EventArgs e) =>
            _ = OpenErrorLogCallbackAsync();

        private Task OpenErrorLogCallbackAsync()
        {
            _hasSeenErrorLogUpdate = true;

            if (_latestUpdatedLogFileStatus != null && File.Exists(_latestUpdatedLogFileStatus.Path))
            {
                System.Diagnostics.Process.Start(_latestUpdatedLogFileStatus.Path);
            }
            else
            {
                DialogHelpers.Error("The log file doesn't exist.", "Open Orchard Error Log");
            }

            return UpdateOpenErrorLogCommandAccessibilityAndTextAsync();
        }

        private void LogWatcherSettingsUpdatedCallback(object sender, LogWatcherSettingsUpdatedEventArgs e) =>
            _ = LogWatcherSettingsUpdatedCallbackAsync(e);

        private async Task LogWatcherSettingsUpdatedCallbackAsync(LogWatcherSettingsUpdatedEventArgs e)
        {
            var isEnabled = e.Settings.LogWatcherEnabled;
            var orchardLogWatcherToolbar = ((CommandBars)(await _package.GetDteAsync()
                .ConfigureAwait(true)).CommandBars)[CommandBarNames.OrchardLogWatcherToolbarName];
            orchardLogWatcherToolbar.Visible = isEnabled;

            // Since this method will be called from the UI thread not blocking it with the watcher changes that can
            // potentially take some time.
            await Task.Run(() =>
            {
                // If the settings are repeatedly change then wait for the first one to finish before starting the next.
                lock (_settingsChangeLock)
                {
                    if (isEnabled)
                    {
                        StartLogFileWatching();
                    }
                    else
                    {
                        StopLogFileWatching();
                    }
                }
            }).ConfigureAwait(true);

            await UpdateOpenErrorLogCommandAccessibilityAndTextAsync().ConfigureAwait(true);
        }

        private async Task UpdateOpenErrorLogCommandAccessibilityAndTextAsync(ILogFileStatus logFileStatus = null)
        {
            var logWatcherSettings = await _logWatcherSettingsAccessor.GetSettingsAsync().ConfigureAwait(true);

            if (!(await _package.GetDteAsync().ConfigureAwait(true)).SolutionIsOpen())
            {
                _openErrorLogCommand.Enabled = false;
                _openErrorLogCommand.Text = "Solution is initializing";
            }
            else if (logWatcherSettings.LogWatcherEnabled &&
                ((logFileStatus?.HasContent ?? false) || !_hasSeenErrorLogUpdate))
            {
                _openErrorLogCommand.Enabled = true;
                _openErrorLogCommand.Text = "Open Orchard error log";

                if (_errorIndicatorStateChanged)
                {
                    if (logWatcherSettings.BlinkBlinkStick) _blinkStickManager.Blink(logWatcherSettings.BlinkStickColor);
                    else _blinkStickManager.TurnOn(logWatcherSettings.BlinkStickColor);
                    _errorIndicatorStateChanged = false;
                }
            }
            else
            {
                _openErrorLogCommand.Enabled = false;
                _openErrorLogCommand.Text = "Orchard error log doesn't exist or hasn't been updated";
                if (!_errorIndicatorStateChanged) _blinkStickManager.TurnOff();
                _errorIndicatorStateChanged = true;
            }
        }

        private void StartLogFileWatching()
        {
            foreach (var watcher in _logWatchers)
            {
                _ = watcher.StartWatchingAsync();
            }
        }

        private void StopLogFileWatching()
        {
            foreach (var watcher in _logWatchers)
            {
                watcher.StopWatching();
            }
        }
    }
}
