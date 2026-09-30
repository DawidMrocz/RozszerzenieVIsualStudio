using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mroczware.PowerFullTool.UI.ViewModel
{
    public class BaseViewModel : INotifyPropertyChanged
    {
        public event EventHandler<bool?> RequestClose;

        public event PropertyChangedEventHandler PropertyChanged;

        public void CloseWindow()
        {
            RequestClose?.Invoke(this, false);
        }

        public virtual void ConfirmSelection()
        {
            RequestClose?.Invoke(this, true);
        }

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
