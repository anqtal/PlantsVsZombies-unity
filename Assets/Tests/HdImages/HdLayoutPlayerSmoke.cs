#nullable enable
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using PlantsVsZombies.Rendering;
using PvZReplica;
using PvZReplica.UI;
using UnityEngine;

namespace PlantsVsZombies.Tests.HdImages
{
    /// <summary>Captures actual UI and combat rendering after startup has completed.</summary>
    internal sealed class HdLayoutPlayerSmoke : MonoBehaviour
    {
        /// <summary>Accesses the host's existing private test surfaces.</summary>
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        /// <summary>The game host in the isolated Player session.</summary>
        private PvZGame controller = null!;

        /// <summary>The first rendering error reported during this scenario.</summary>
        private string? loggedError;

        /// <summary>Creates this scenario only alongside the existing isolated UI review mode.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var args = Environment.GetCommandLineArgs();
            if (Array.IndexOf(args, "-pvz-hd-layout-smoke") >= 0 && Array.IndexOf(args, "-pvz-ui-review") >= 0)
            {
                new GameObject("HD layout Player smoke").AddComponent<HdLayoutPlayerSmoke>();
            }
        }

        private IEnumerator Start()
        {
            yield return null;
            controller = FindFirstObjectByType<PvZGame>();
            Application.logMessageReceived += OnLog;
            var steps = Run();
            while (true)
            {
                bool next;
                try
                {
                    next = steps.MoveNext();
                }
                catch (Exception error)
                {
                    Debug.LogError("PVZ_HD_LAYOUT_SMOKE_FAIL: " + error);
                    SmokeTestArtifacts.Quit(1);
                    yield break;
                }
                if (!next)
                {
                    break;
                }
                yield return steps.Current;
            }
            if (loggedError != null)
            {
                Debug.LogError("PVZ_HD_LAYOUT_SMOKE_FAIL: " + loggedError);
                SmokeTestArtifacts.Quit(1);
                yield break;
            }
            Debug.Log("PVZ_HD_LAYOUT_SMOKE_PASS: selector, help, options, almanac and native combat; HD loads=" + HighResolutionTextures.LoadedCount);
            SmokeTestArtifacts.Quit(0);
        }

        /// <summary>Exercises the real UI hosts and real native combat renderer.</summary>
        /// <returns>Frame waits for startup and screenshot capture.</returns>
        private IEnumerator Run()
        {
            Require(controller != null, "Game controller was not created.");
            var deadline = Time.realtimeSinceStartup + 60f;
            while (controller.OriginalLoadingActive && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            Require(!controller.OriginalLoadingActive, "Startup did not finish.");
            Time.timeScale = 0f;
            SetScreen("Selector");
            yield return Capture("selector");
            Call("HandleSelector", SelectorAction.Help);
            yield return Capture("help");
            Call("HandleHelpPointer", HelpView.ReturnButtonBounds.center, EventType.MouseDown);
            Call("HandleHelpPointer", HelpView.ReturnButtonBounds.center, EventType.MouseUp);
            SetScreen("Selector");
            Call("HandleSelector", SelectorAction.Options);
            yield return Capture("options");
            Call("CloseOptions");
            SetScreen("Selector");
            Call("HandleSelector", SelectorAction.Almanac);
            yield return Capture("almanac");
            controller.StartAdventureTestLevel(8);
            var game = (GameSimulation)typeof(PvZGame).GetField("game", Private)!.GetValue(controller);
            Require(game.ChooseDefaultAdventureSeeds(), "Native combat seed selection failed.");
            typeof(PvZGame).GetField("choosingSeeds", Private)!.SetValue(controller, false);
            SimCall(game, "PlaceAdventurePlant", PlantKind.Peashooter, 1, 1);
            SimCall(game, "PlaceAdventurePlant", PlantKind.Sunflower, 2, 1);
            SimCall(game, "PlaceAdventurePlant", PlantKind.Wallnut, 3, 1);
            SimCall(game, "SpawnAdventureZombie", ZombieKind.Normal, 1, 6.5f, 0);
            SimCall(game, "SpawnAdventureZombie", ZombieKind.Conehead, 3, 6.5f, 0);
            Require(game.Plants.Count == 3 && game.Zombies.Count == 2, "Combat fixture was not populated.");
            yield return Capture("native-combat");
            if (HighResolutionTextures.Enabled)
            {
                Require(HighResolutionTextures.LoadedCount > 20, "The game did not use the HD resource pack.");
            }
        }

        /// <summary>Captures a completed, nonblank game frame.</summary>
        /// <param name="name">The screenshot's scenario name.</param>
        /// <returns>A frame boundary before capture.</returns>
        private IEnumerator Capture(string name)
        {
            yield return new WaitForEndOfFrame();
            var image = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                var pixels = image.GetPixels32();
                var visible = 0;
                foreach (var pixel in pixels)
                {
                    if (pixel.r + pixel.g + pixel.b > 24)
                    {
                        visible++;
                    }
                }
                Require(visible > pixels.Length / 10, "Blank rendering: " + name);
                File.WriteAllBytes(Path.Combine(SmokeTestArtifacts.DirectoryPath, name + ".png"), image.EncodeToPNG());
            }
            finally
            {
                Destroy(image);
            }
        }

        /// <summary>Selects an existing host screen without changing layout coordinates.</summary>
        /// <param name="name">The host enum value.</param>
        private void SetScreen(string name)
        {
            var field = typeof(PvZGame).GetField("currentView", Private)!;
            field.SetValue(controller, Enum.Parse(field.FieldType, name));
        }

        /// <summary>Invokes an existing game-host action.</summary>
        /// <param name="name">The private host method.</param>
        /// <param name="args">The action's arguments.</param>
        private void Call(string name, params object[] args)
        {
            typeof(PvZGame).GetMethod(name, Private)!.Invoke(controller, args);
        }

        /// <summary>Populates a deterministic native combat fixture using existing test helpers.</summary>
        /// <param name="game">The active simulation.</param>
        /// <param name="name">The existing private fixture helper.</param>
        /// <param name="args">The supplied helper arguments.</param>
        private static void SimCall(GameSimulation game, string name, params object[] args)
        {
            var method = typeof(GameSimulation).GetMethod(name, Private)!;
            var parameters = method.GetParameters();
            var supplied = args.Length;
            Array.Resize(ref args, parameters.Length);
            for (var index = supplied; index < parameters.Length; index++)
            {
                args[index] = parameters[index].DefaultValue;
            }
            method.Invoke(game, args);
        }

        /// <summary>Records rendering errors for the scenario result.</summary>
        /// <param name="message">The logged message.</param>
        /// <param name="stack">The associated stack trace.</param>
        /// <param name="type">The Unity log severity.</param>
        private void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                loggedError = message;
            }
        }

        private void OnDestroy()
        {
            Application.logMessageReceived -= OnLog;
        }

        /// <summary>Fails the scenario when a rendering invariant is false.</summary>
        /// <param name="condition">The required invariant.</param>
        /// <param name="message">The failure description.</param>
        /// <exception cref="InvalidOperationException">The required invariant is false.</exception>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
