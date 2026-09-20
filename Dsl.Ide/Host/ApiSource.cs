using System;
using System.IO;
using Dsl.Compilation;
using Dsl.Semantics;
using Newtonsoft.Json;

namespace Dsl.Ide
{
    /// <summary>
    /// Что IDE знает об API хоста: DTO манифеста — для подсказок и hover,
    /// реестр — для компиляции. Неизменяемый снимок: смена API = новый объект,
    /// поэтому фоновая компиляция спокойно держит ссылку на старый.
    /// </summary>
    public sealed class ApiModel
    {
        /// <summary>Манифест (подсказки); null — API неизвестен.</summary>
        public readonly ApiManifest Manifest;

        /// <summary>Реестр для компилятора (не null). Компилятор его только читает.</summary>
        public readonly HostRegistry Registry;

        public readonly int ApiVersion;

        /// <summary>Откуда взят: для строки статуса.</summary>
        public readonly string Label;

        public ApiModel(ApiManifest manifest, HostRegistry registry, int apiVersion, string label)
        {
            Manifest = manifest;
            Registry = registry ?? new HostRegistry();
            ApiVersion = apiVersion;
            Label = label;
        }

        /// <summary>Без API: язык и Engine.* проверяются, события и API игры — нет.</summary>
        public static ApiModel Empty => new ApiModel(null, new HostRegistry(), 1, "API игры не задан");

        public bool IsEmpty => Manifest == null;

        /// <summary>Из текста salamander-api.json. Бросает на битом манифесте.</summary>
        public static ApiModel FromJson(string json, string label)
        {
            var registry = ApiManifest.Import(json, out int version);
            var manifest = JsonConvert.DeserializeObject<ApiManifest>(json);
            return new ApiModel(manifest, registry, version, label);
        }

        /// <summary>
        /// Из живого реестра игры, без файла salamander-api.json: реестр
        /// экспортируется в манифест и импортируется обратно в реестр-заглушку.
        /// Компиляция IDE идёт в фоновом потоке, а живой реестр принадлежит игре
        /// (хост может дорегистрировать API по фазам) — поэтому фон читает только
        /// свою копию и никогда не делит объект с главным потоком.
        /// </summary>
        public static ApiModel FromRegistry(HostRegistry registry, int apiVersion, string label)
        {
            string json = ApiManifest.Export(registry, apiVersion);
            var stub = ApiManifest.Import(json, out _);
            var manifest = JsonConvert.DeserializeObject<ApiManifest>(json);
            return new ApiModel(manifest, stub, apiVersion, label);
        }
    }

    /// <summary>Источник API. Poll зовётся с главного потока раз в пару секунд.</summary>
    public interface IApiSource
    {
        ApiModel Current { get; }

        /// <summary>Последняя ошибка загрузки (null — всё хорошо).</summary>
        string Error { get; }

        /// <summary>true — модель сменилась.</summary>
        bool Poll();
    }

    public sealed class FixedApiSource : IApiSource
    {
        public FixedApiSource(ApiModel model) => Current = model ?? ApiModel.Empty;
        public ApiModel Current { get; }
        public string Error => null;
        public bool Poll() => false;
    }

    /// <summary>
    /// salamander-api.json на диске с авто-обновлением. При старте ВСЕГДА читается
    /// сам файл (правки, сделанные пока IDE была закрыта, подхватываются сразу);
    /// копия в хранилище — только запасной вариант, если файл пропал.
    /// Битая версия файла не затирает рабочую модель и не перечитывается, пока
    /// файл снова не изменится.
    /// </summary>
    public sealed class ManifestFileApiSource : IApiSource
    {
        private const string CacheKey = "api-cache.json";

        private readonly IIdeStorage _cache;
        private FileStamp _stamp = FileStamp.None;

        public string Path { get; }
        public ApiModel Current { get; private set; } = ApiModel.Empty;
        public string Error { get; private set; }

        public ManifestFileApiSource(string path, IIdeStorage cache = null)
        {
            Path = string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFullPath(path);
            _cache = cache;
            Reload(initial: true);
        }

        public bool Poll()
        {
            if (Path == null) return false;
            var s = FileStamp.Of(Path);
            if (s.Equals(_stamp)) return false;
            return Reload(initial: false);
        }

        private bool Reload(bool initial)
        {
            if (Path == null) return false;
            _stamp = FileStamp.Of(Path);
            string name = System.IO.Path.GetFileName(Path);
            if (_stamp.IsNone)
            {
                // файла нет: при старте поднимаем копию, в работе — оставляем что было
                if (initial && _cache != null)
                {
                    string cached = _cache.Read(CacheKey);
                    if (!string.IsNullOrEmpty(cached))
                    {
                        try
                        {
                            Current = ApiModel.FromJson(cached, name + " (копия — оригинал не найден)");
                            Error = "оригинал манифеста не найден: " + Path;
                            return true;
                        }
                        catch (Exception e) { Error = "копия манифеста битая: " + e.Message; }
                    }
                }
                Error = "манифест не найден: " + Path;
                return false;
            }
            try
            {
                string json = File.ReadAllText(Path);
                var model = ApiModel.FromJson(json, name);
                Current = model;
                Error = null;
                _cache?.Write(CacheKey, json);
                return true;
            }
            catch (Exception e)
            {
                // битый файл не затирает рабочую модель; повтор — только после следующей правки файла
                Error = "манифест не читается: " + e.Message;
                return false;
            }
        }
    }
}
