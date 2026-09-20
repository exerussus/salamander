using System;
using System.Threading;
using UnityEngine;

namespace Dsl.Ide
{
    public enum IdeLogLevel { Trace, Info, Warning, Error }

    public enum IdeStatusKind { Info, Ok, Warning, Error }

    /// <summary>
    /// Всё, что IDE берёт у окружения. IDE одна и та же в игре и в редакторе;
    /// различается только хост: в игре — <see cref="DefaultIdeHost"/>, в редакторе
    /// (страница Nexus) — редакторский хост с импортом ассетов и логом Nexus.
    /// Хост не знает про устройство IDE — только отвечает на эти вопросы.
    /// </summary>
    public interface IIdeHost
    {
        /// <summary>Лог самой IDE (не скриптов): сбои чтения, записи, компиляции.</summary>
        void Log(IdeLogLevel level, string context, string message, Exception ex = null);

        /// <summary>Статус IDE продублирован хосту (у IDE есть и своя строка статуса).</summary>
        void OnStatus(string text, IdeStatusKind kind);

        /// <summary>Файл записан на диск. Редактор импортирует ассет, игра — ничего.</summary>
        void OnFileSaved(string path);

        /// <summary>Можно ли компилировать в фоновом потоке (WebGL — нельзя).</summary>
        bool SupportsThreads { get; }

        /// <summary>
        /// Прервать зависший поток компиляции. Умеет только редактор на Mono
        /// (перед перезагрузкой домена); в игре потоки не прерываются — зависший
        /// бросается. true — поток прерван.
        /// </summary>
        bool TryAbortThread(Thread thread);
    }

    /// <summary>Хост по умолчанию: игра (и любой хост, которому нечего добавить).</summary>
    public class DefaultIdeHost : IIdeHost
    {
        public virtual void Log(IdeLogLevel level, string context, string message, Exception ex = null)
        {
            string line = $"[salamander-ide] {context}: {message}" + (ex != null ? $" — {ex.Message}" : "");
            switch (level)
            {
                case IdeLogLevel.Error: Debug.LogError(line); break;
                case IdeLogLevel.Warning: Debug.LogWarning(line); break;
                case IdeLogLevel.Info: Debug.Log(line); break;
                // Trace — только в отладочной сборке, чтобы не шуметь в логе игрока
                default: if (Debug.isDebugBuild) Debug.Log(line); break;
            }
        }

        public virtual void OnStatus(string text, IdeStatusKind kind) { }

        public virtual void OnFileSaved(string path) { }

        public virtual bool SupportsThreads => Application.platform != RuntimePlatform.WebGLPlayer;

        public virtual bool TryAbortThread(Thread thread) => false;
    }

    /// <summary>
    /// Цель кнопки «Применить»: перекомпилировать и перезагрузить скрипты в
    /// работающей игре. Даёт мост к ScriptHostBootstrap; в редакторе вне Play
    /// её обычно нет — кнопка тогда скрыта.
    /// </summary>
    public interface IIdeApplyTarget
    {
        bool CanApply { get; }
        string ApplyLabel { get; }
        void Apply();
    }
}
