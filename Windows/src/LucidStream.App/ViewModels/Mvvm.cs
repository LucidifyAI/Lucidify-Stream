using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LucidStream.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base (no toolkit packages needed).</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Command that runs an async action, prevents re-entry, and reports failures instead of crashing.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public RelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public static RelayCommand Sync(Action<object?> execute, Func<object?, bool>? canExecute = null, Action<Exception>? onError = null) =>
        new(p => { execute(p); return Task.CompletedTask; }, canExecute, onError);

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        Raise();
        try { await _execute(parameter); }
        catch (Exception ex) { _onError?.Invoke(ex); }
        finally
        {
            _running = false;
            Raise();
        }
    }

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}