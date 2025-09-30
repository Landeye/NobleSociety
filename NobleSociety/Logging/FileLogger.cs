using System;
using System.IO;

namespace NobleSociety.Logging
{
    public static class FileLogger
    {
        private static readonly string LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Mount and Blade II Bannerlord",
            "Logs"
        );

        private static readonly string LogPath = Path.Combine(LogDirectory, "NobleSocietyLog.txt");

        static FileLogger()
        {
            Directory.CreateDirectory(LogDirectory);
        }

        public static void Log(string message)
        {

            using (StreamWriter writer = new StreamWriter(LogPath, append: true))
            {
                writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}");
            }

        }
    }
}

