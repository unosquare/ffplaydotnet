namespace Unosquare.FFplayDotNet.Sample
{
    using System;
    using System.Windows;
    using System.Windows.Input;

    public class DelegateCommand : ICommand
    {
        public event EventHandler CanExecuteChanged = null;

        private Action<object> m_Action = null;
        private Func<object, bool> m_CanExecute = null;

        public DelegateCommand(Action<object> action, Func<object, bool> canExecute)
        {
            m_Action = action;
            m_CanExecute = canExecute;
        }

        public bool CanExecute(object parameter)
        {
            if (m_CanExecute == null) return true;
            return
                Application.Current?.Dispatcher?.Invoke(() => { return m_CanExecute(parameter); }) ?? false;
        }

        public void Execute(object parameter)
        {
            if (m_Action == null) return;
            Application.Current?.Dispatcher?.Invoke(() => { m_Action(parameter); });
        }
    }
}
