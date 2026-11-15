using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

/// <summary>
/// Hosts a single persistent WPF Application and STA Dispatcher for the entire test AppDomain so
/// UI tests never recreate or prematurely shut down System.Windows.Application across test classes.
/// </summary>
internal static class WpfTestApplication
{
    private static readonly object Sync = new();
    private static readonly ManualResetEventSlim Ready = new(false);
    private static Thread? staThread;
    private static Dispatcher? staDispatcher;
    private static Application? application;

    public static void Run(Action action, TimeSpan? timeout = null, bool loadStudioTheme = false)
    {
        EnsureHost();
        Exception? failure = null;
        var completed = staDispatcher!.BeginInvoke(new Action(() =>
        {
            try
            {
                if (loadStudioTheme)
                    ApplyStudioThemeResources();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }));
        var status = completed.Wait(timeout ?? TimeSpan.FromSeconds(30));
        if (status != DispatcherOperationStatus.Completed)
            throw new AssertFailedException("WPF UI test timed out on shared STA dispatcher.");
        if (failure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void EnsureHost()
    {
        lock (Sync)
        {
            if (staThread != null)
                return;
            staThread = new Thread(() =>
            {
                application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                staDispatcher = application.Dispatcher;
                Ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "CompanionTests.WpfSta"
            };
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            Ready.Wait();
        }
    }

    private static void ApplyStudioThemeResources()
    {
        var theme = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "studio-theme.xaml"));
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = new XElement(
            wpf + "ResourceDictionary",
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            theme.Root!.Element(wpf + "Application.Resources")!.Elements());
        application!.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
    }
}
