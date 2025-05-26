using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace ProxChatClient.ViewModels
{
    public class PeerViewModel : INotifyPropertyChanged
    {
        public const string DefaultCharacterName = "Unknown"; // Or a more specific placeholder like "LoadingName..."
        private string _id = string.Empty;
        private string _characterName = DefaultCharacterName;
        private bool _isMuted;
        private float _distance;
        private float _volume = 0.5f; // Default volume to 0.5f to leave room for boosting specific peers
        private int _mapId;
        private int _x;
        private int _y;
        private bool _isTransmitting = false; // Track if peer is actively transmitting audio

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string CharacterName
        {
            get => _characterName;
            set { _characterName = value; OnPropertyChanged(); }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (_isMuted != value)
                {
                    _isMuted = value;
                    OnPropertyChanged();
                    // Notify MainViewModel to update AudioService and persist
                    if (Application.Current.MainWindow.DataContext is MainViewModel mainVm)
                    {
                        mainVm.UpdatePeerMuteStateFromViewModel(Id, _isMuted);
                    }
                }
            }
        }

        public float Distance
        {
            get => _distance;
            set 
            {
                if (_distance != value)
                {
                    _distance = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(DistanceDisplay)); 
                }
            }
        }

        public float Volume
        {
            get => _volume;
            set
            {
                float clampedValue = Math.Clamp(value, 0.0f, 1.0f);
                if (_volume != clampedValue)
                {
                    _volume = clampedValue;
                    OnPropertyChanged();
                    // Trigger update in AudioService via MainViewModel
                    if (Application.Current.MainWindow.DataContext is MainViewModel mainVm)
                    {
                        mainVm.UpdatePeerVolumeFromViewModel(Id, _volume);
                    }
                }
            }
        }

        public int MapId
        {
            get => _mapId;
            set { _mapId = value; OnPropertyChanged(); }
        }

        public int X
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); }
        }

        public int Y
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); }
        }

        public bool IsTransmitting
        {
            get => _isTransmitting;
            set 
            { 
                if (_isTransmitting != value)
                {
                    _isTransmitting = value; 
                    OnPropertyChanged(); 
                }
            }
        }

        public string DistanceDisplay => Distance < 0 ? "Different Map" : $"{Distance:F1}";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            Application.Current.Dispatcher.Invoke(() => // Ensure UI updates on the UI thread
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            });
        }
    }
} 