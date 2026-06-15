using System;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Win32;

namespace PSControllerUI
{
    public class AppSettings
    {
        public bool ShowNotifications { get; set; } = true;
        public bool MinimizeOnClose { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public bool RunAtStartup { get; set; } = false;
    }

    public class SettingsManager
    {
        private static readonly string FolderPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MonarchButterfly"
        );
        private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");
        private static readonly JsonSerializerOptions SerializeOptions = new JsonSerializerOptions { WriteIndented = true };

        public static AppSettings Instance { get; private set; } = new AppSettings();

        static SettingsManager()
        {
            Load();
        }

        public static void Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        Instance = settings;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to load settings: {ex.Message}");
            }
            Instance = new AppSettings();
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(FolderPath))
                {
                    Directory.CreateDirectory(FolderPath);
                }

                string json = JsonSerializer.Serialize(Instance, SerializeOptions);
                File.WriteAllText(FilePath, json);

                // Apply startup registry setting
                ApplyStartupRegistry(Instance.RunAtStartup);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save settings: {ex.Message}");
            }
        }

        private static void ApplyStartupRegistry(bool runAtStartup)
        {
            const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string AppName = "MonarchButterfly";

            try
            {
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key != null)
                    {
                        if (runAtStartup)
                        {
                            string path = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                            if (!string.IsNullOrEmpty(path))
                            {
                                key.SetValue(AppName, $"\"{path}\"");
                            }
                        }
                        else
                        {
                            key.DeleteValue(AppName, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set startup registry key: {ex.Message}");
            }
        }
    }
}
