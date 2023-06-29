﻿using System;
using AutoUpdaterDotNET;
using StabilityMatrix.Models;

namespace StabilityMatrix.Helper;

public class EventManager
{
    public static EventManager Instance { get; } = new();

    private EventManager()
    {

    }
    
    public event EventHandler<int>? GlobalProgressChanged;
    public event EventHandler<Type>? PageChangeRequested;
    public event EventHandler? InstalledPackagesChanged;
    public event EventHandler? OneClickInstallFinished;
    public event EventHandler? TeachingTooltipNeeded;
    public event EventHandler<bool>? DevModeSettingChanged;
    public event EventHandler<UpdateInfo>? UpdateAvailable;
    public void OnGlobalProgressChanged(int progress) => GlobalProgressChanged?.Invoke(this, progress);
    public void RequestPageChange(Type pageType) => PageChangeRequested?.Invoke(this, pageType);
    public void OnInstalledPackagesChanged() => InstalledPackagesChanged?.Invoke(this, EventArgs.Empty);
    public void OnOneClickInstallFinished() => OneClickInstallFinished?.Invoke(this, EventArgs.Empty);
    public void OnTeachingTooltipNeeded() => TeachingTooltipNeeded?.Invoke(this, EventArgs.Empty);
    public void OnDevModeSettingChanged(bool value) => DevModeSettingChanged?.Invoke(this, value);
    public void OnUpdateAvailable(UpdateInfo args) => UpdateAvailable?.Invoke(this, args);
}
