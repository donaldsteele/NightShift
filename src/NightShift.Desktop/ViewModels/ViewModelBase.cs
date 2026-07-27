using CommunityToolkit.Mvvm.ComponentModel;
using NightShift.Desktop.Services;

namespace NightShift.Desktop.ViewModels;

/// <summary>
/// Base for every view model, with the one piece of thread discipline they all need.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia marshals ordinary <c>INotifyPropertyChanged</c> updates for us, so mutating an
/// <c>[ObservableProperty]</c> off the UI thread usually appears to work. Two things it does
/// <b>not</b> forgive, both found by driving the real app rather than by a test:
/// </para>
/// <list type="number">
/// <item><c>IRelayCommand.NotifyCanExecuteChanged()</c> raises <c>CanExecuteChanged</c>
/// synchronously on the calling thread, and Avalonia throws <c>InvalidOperationException</c> when a
/// bound command does that from a background thread.</item>
/// <item>Rebuilding a bound <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// off the UI thread corrupts the target: the notifications are <i>posted</i>, so the <c>Reset</c>
/// from <c>Clear()</c> arrives after the collection has already been refilled and the queued
/// <c>Add</c>s are replayed — twelve preflight pills rendered as twenty-four.</item>
/// </list>
/// <para>
/// Both bite specifically after <c>await … ConfigureAwait(false)</c>, where the continuation lands
/// on a thread-pool thread. Route those mutations through <see cref="OnUiThread"/>.
/// </para>
/// </remarks>
public abstract class ViewModelBase : ObservableObject
{
    readonly IUiDispatcher? _uiDispatcher;

    /// <summary>For view models with no bound collections and no command re-evaluation.</summary>
    protected ViewModelBase()
    {
    }

    protected ViewModelBase(IUiDispatcher uiDispatcher) =>
        _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));

    /// <summary>
    /// Runs <paramref name="action"/> where it is safe to touch bound state: inline when already on
    /// the UI thread, posted otherwise. Cheap enough to use unconditionally — the check is a thread
    /// comparison, and being wrong is either a crash or a silently duplicated list.
    /// </summary>
    protected void OnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_uiDispatcher is null || _uiDispatcher.IsOnUiThread)
        {
            action();
            return;
        }

        _uiDispatcher.Post(action);
    }
}
