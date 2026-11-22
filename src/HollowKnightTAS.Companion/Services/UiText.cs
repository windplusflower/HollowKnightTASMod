using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using HollowKnightTAS.Core.Localization;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class UiText : INotifyPropertyChanged
    {
        public sealed record Entry(string Key, string Source, string Zh, string En);
        public static UiText Current { get; } = new UiText(DefaultPath, GameLanguageReader.DetectStudioLanguageIndex);
        private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HollowKnightTAS", "studio-language.txt");
        private readonly string path;
        private readonly Dictionary<string, Entry> keys;
        private readonly Dictionary<string, Entry> exact;
        private readonly List<(Regex Pattern, Entry Entry)> templates;
        private int languageIndex;
        public UiText(string path) : this(path, defaultLanguageDetector: null) { }
        public UiText(string path, Func<int>? defaultLanguageDetector)
        {
            this.path = path;
            using var stream = typeof(UiText).Assembly.GetManifestResourceStream("HollowKnightTAS.Companion.UiText.json")!;
            var entries = JsonSerializer.Deserialize<Entry[]>(stream)!;
            keys = entries.ToDictionary(e => e.Key);
            exact = entries.GroupBy(e => e.Source).ToDictionary(g => g.Key, g => g.First());
            templates = entries.Where(e => e.Source.Contains("{0}"))
                .OrderByDescending(e => e.Source.Length).Select(e =>
                {
                    var pattern = Regex.Escape(e.Source);
                    for (var i = 0; i < 10; i++) pattern = pattern.Replace(Regex.Escape("{" + i + "}"), "(?<p" + i + ">.*?)");
                    return (new Regex("^" + pattern + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromMilliseconds(50)), e);
                }).ToList();
            try
            {
                if (File.Exists(path))
                {
                    var saved = File.ReadAllText(path).Trim().ToLowerInvariant();
                    if (saved == "zh")
                    {
                        languageIndex = GameLanguageDetector.ChineseStudioIndex;
                        HasExplicitPreference = true;
                        return;
                    }
                    if (saved == "en")
                    {
                        languageIndex = GameLanguageDetector.EnglishStudioIndex;
                        HasExplicitPreference = true;
                        return;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            HasExplicitPreference = false;
            languageIndex = ResolveAutoDefault(defaultLanguageDetector);
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public int Revision { get; private set; }
        public bool HasExplicitPreference { get; private set; }
        public string[] Languages { get; } = { "中文", "English" };
        public string SettingsError { get; private set; } = "";
        public int LanguageIndex
        {
            get => languageIndex;
            set
            {
                if (value is < 0 or > 1 || value == languageIndex) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path + ".new", value == 1 ? "en" : "zh");
                    File.Move(path + ".new", path, true);
                    languageIndex = value;
                    HasExplicitPreference = true;
                    SettingsError = "";
                    NotifyLanguageChanged();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    SettingsError = Translate("语言设置保存失败：") + ex.Message;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SettingsError)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LanguageIndex)));
                }
            }
        }
        public bool ApplyAutoDetectedGameLanguage(string? gameLanguageCode)
        {
            if (HasExplicitPreference || string.IsNullOrWhiteSpace(gameLanguageCode))
            {
                return false;
            }

            var target = GameLanguageDetector.ToStudioLanguageIndex(gameLanguageCode);
            if (target == languageIndex)
            {
                return false;
            }

            languageIndex = target;
            NotifyLanguageChanged();
            return true;
        }
        private static int ResolveAutoDefault(Func<int>? detector)
        {
            if (detector == null)
            {
                return GameLanguageDetector.ChineseStudioIndex;
            }

            try
            {
                return detector() == GameLanguageDetector.EnglishStudioIndex
                    ? GameLanguageDetector.EnglishStudioIndex
                    : GameLanguageDetector.ChineseStudioIndex;
            }
            catch
            {
                return GameLanguageDetector.ChineseStudioIndex;
            }
        }
        private void NotifyLanguageChanged()
        {
            Revision++;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
        public string this[string key] => keys.TryGetValue(key, out var entry) ? Pick(entry) : key;
        private string Pick(Entry entry) => languageIndex == 1 ? entry.En : entry.Zh;
        public static string T(string text) => Current.Translate(text);
        public string Translate(string text) => Translate(text, 0);
        private string Translate(string text, int depth)
        {
            if (exact.TryGetValue(text, out var entry)) return Pick(entry);
            if (depth >= 8) return text;
            foreach (var template in templates)
            {
                var match = template.Pattern.Match(text);
                if (!match.Success) continue;
                var result = Pick(template.Entry);
                for (var i = 0; i < 10; i++)
                {
                    var value = match.Groups["p" + i].Value;
                    if (value.Length < text.Length) value = Translate(value, depth + 1);
                    result = result.Replace("{" + i + "}", value);
                }
                return result;
            }
            if (text.Contains('\n')) return string.Join("\n", text.Split('\n').Select(line => Translate(line, depth + 1)));
            return text; // Filenames, key names and Runtime diagnostic identifiers are data.
        }
    }
    public sealed class TextExtension : MarkupExtension
    {
        public TextExtension(string key) { Key = key; }
        public string Key { get; set; }
        public override object ProvideValue(IServiceProvider provider) => new Binding("[" + Key + "]")
            { Source = UiText.Current, Mode = BindingMode.OneWay }.ProvideValue(provider);
    }
    public sealed class TextBindingExtension : MarkupExtension
    {
        public TextBindingExtension(string path) { Path = path; }
        public string Path { get; set; }
        public override object ProvideValue(IServiceProvider provider)
        {
            var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new Translator() };
            binding.Bindings.Add(new Binding(Path));
            binding.Bindings.Add(new Binding(nameof(UiText.Revision)) { Source = UiText.Current });
            return binding.ProvideValue(provider);
        }
        private sealed class Translator : IMultiValueConverter
        {
            public object Convert(object[] values, Type type, object parameter, CultureInfo culture)
                => values[0] is string text ? UiText.T(text) : values[0];
            public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }
    }
}
