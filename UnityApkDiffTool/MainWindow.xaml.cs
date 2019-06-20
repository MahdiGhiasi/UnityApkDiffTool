using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;

namespace UnityApkDiffTool
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        string outputHtmlPath;

        public readonly string[] ignoreLineRegexPatterns = new[] {
            @"^[ \t]*\[AsyncStateMachine\(typeof",
            @"^[ \t]*//.*Token:",
            @"^[ \t]*\[YAX",
        };

        public MainWindow()
        {
            InitializeComponent();

            selectView.Visibility = Visibility.Visible;
            resultsView.Visibility = Visibility.Collapsed;

            darkModeCheckBox.IsChecked = Properties.Settings.Default.DarkMode;
            showDiffOnlyCheckBox.IsChecked = Properties.Settings.Default.ShowDiffOnly;
        }

        private void SaveSettings()
        {
            Properties.Settings.Default.DarkMode = darkModeCheckBox.IsChecked ?? false;
            Properties.Settings.Default.ShowDiffOnly = showDiffOnlyCheckBox.IsChecked ?? true;
            Properties.Settings.Default.Save();
        }

        private string PreprocessCode(string code1)
        {
            var lines = code1.Split(new char[] { '\n' });
            string output = "";

            foreach (var item in lines)
            {
                var line = item;

                var ignore = false;
                foreach (var pattern in ignoreLineRegexPatterns)
                {
                    var match = Regex.Match(line, pattern);
                    if (match.Success)
                    {
                        ignore = true;
                        break;
                    }
                }
                if (ignore)
                    continue;

                output += line + "\n";
            }
            return output;
        }

        public string GetDiff(string title, string code1, string code2)
        {
            var diffBuilder = new InlineDiffBuilder(new Differ());
            var diff = diffBuilder.BuildDiffModel(code1, code2);

            var codes = "";
            var lineNumbers = "";

            int counter = 1;

            int? firstChange = null;

            foreach (var line in diff.Lines)
            {
                var codeLineNumber = $"{counter}:";
                var codeClass = "";
                var codeLineClass = $"line{counter}";

                switch (line.Type)
                {
                    case ChangeType.Inserted:
                        codeClass = "addedLine";
                        if (firstChange == null)
                            firstChange = counter;
                        break;
                    case ChangeType.Deleted:
                        codeClass = "removedLine";
                        if (firstChange == null)
                            firstChange = counter;
                        break;
                    default:
                        break;
                }

                var htmlCodeLine = WebUtility.HtmlEncode(line.Text + "\n")
                    .Replace("\t", "&nbsp;&nbsp;&nbsp;&nbsp;")
                    .Replace(" ", "&nbsp;");

                var htmlLineNum = WebUtility.HtmlEncode(codeLineNumber + "\n")
                    .Replace("\t", "&nbsp;&nbsp;&nbsp;&nbsp;")
                    .Replace(" ", "&nbsp;");

                codes += $"<div class='codeLine {codeClass} {codeLineClass}'>{htmlCodeLine}</div>";
                lineNumbers += $"<a name='line{counter}'></a><div class='codeLine {codeClass}'>{htmlLineNum}</div>";

                counter++;
            }

            if (!firstChange.HasValue)
                return "";

            var html = File.ReadAllText("diffPageTemplate.html")
                .Replace("{{CODELINES}}", lineNumbers)
                .Replace("{{CODE}}", codes)
                .Replace("{{TITLE}}", WebUtility.HtmlEncode(title))
                .Replace("{{SCROLLTOLINE}}", ((firstChange ?? 0) - 10).ToString());

            return html;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
        }

        private void ExtractApk(string apkFile, string outputFolder)
        {
            if (!Directory.Exists(outputFolder))
                Directory.CreateDirectory(outputFolder);

            FastZip fastZip = new FastZip();
            string fileFilter = null;

            // Will always overwrite if target filenames already exist
            fastZip.ExtractZip(apkFile, outputFolder, fileFilter);
        }

        private static string InitTempDirectory()
        {
            var location = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            var tempLocation = Path.Combine(location, "Temp");

            if (Directory.Exists(tempLocation))
            {
                try
                {
                    Directory.Delete(tempLocation, true);
                }
                catch { }
            }

            var tempLocationForThisSession = Path.Combine(tempLocation, DateTime.Now.ToString("yyyy-MM-dd--hh-mm-ss"));
            Directory.CreateDirectory(tempLocationForThisSession);

            return tempLocationForThisSession;
        }

        private void DecompileAsProject(string assemblyFileName, string outputDirectory, string[] referencePaths)
        {
            if (!Directory.Exists(outputDirectory))
                Directory.CreateDirectory(outputDirectory);

            var decompiler = new WholeProjectDecompiler();
            var module = new PEFile(assemblyFileName);
            var resolver = new UniversalAssemblyResolver(assemblyFileName, false, module.Reader.DetectTargetFrameworkId());
            foreach (var path in referencePaths)
            {
                resolver.AddSearchDirectory(path);
            }
            decompiler.AssemblyResolver = resolver;
            decompiler.DecompileProject(module, outputDirectory);
        }

        private void GenerateDiffForProjects(string proj1Path, string proj2Path, string outPath)
        {
            if (!Directory.Exists(outPath))
                Directory.CreateDirectory(outPath);

            outputHtmlPath = outPath;

            var path1Files = Directory.GetFiles(proj1Path, "*.cs", SearchOption.AllDirectories);
            var path2Files = Directory.GetFiles(proj2Path, "*.cs", SearchOption.AllDirectories);

            var sameFiles = (from ff1 in path1Files
                             from ff2 in path2Files
                             let f1 = ff1.Substring(proj1Path.Length + 1)
                             let f2 = ff2.Substring(proj2Path.Length + 1)
                             where f1 == f2
                             select f1).ToList();

            var removedFiles = (from ff in path1Files
                                let f = ff.Substring(proj1Path.Length + 1)
                                where !path2Files.Any(x => x.Substring(proj2Path.Length + 1) == f)
                                select f).ToList();

            var addedFiles = (from ff in path2Files
                              let f = ff.Substring(proj2Path.Length + 1)
                              where !path1Files.Any(x => x.Substring(proj1Path.Length + 1) == f)
                              select f).ToList();

            foreach (var item in sameFiles)
            {
                var code1 = File.ReadAllText(Path.Combine(proj1Path, item));
                var code2 = File.ReadAllText(Path.Combine(proj2Path, item));

                code1 = PreprocessCode(code1);
                code2 = PreprocessCode(code2);

                var diff = GetDiff(item, code1, code2);

                if (diff.Length == 0)
                    continue;

                var outputFolder = Path.Combine(outPath, Path.GetDirectoryName(item));
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                var outputFile = Path.Combine(outputFolder, Path.GetFileName(item) + ".html");

                var outputName = outputFile.Substring(outPath.Length + 1);
                outputName = outputName.Substring(0, outputName.Length - 5);

                File.WriteAllText(outputFile, diff);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    itemsList.Items.Add(outputName);
                }));
            }

            foreach (var item in removedFiles)
            {
                var code1 = File.ReadAllText(Path.Combine(proj1Path, item));

                code1 = PreprocessCode(code1);

                var diff = GetDiff(item, code1, "");

                if (diff.Length == 0)
                    continue;

                var outputFolder = Path.Combine(outPath, Path.GetDirectoryName(item));
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                var outputFile = Path.Combine(outputFolder, Path.GetFileName(item) + ".html");

                var outputName = outputFile.Substring(outPath.Length + 1);
                outputName = outputName.Substring(0, outputName.Length - 5);

                File.WriteAllText(outputFile, diff);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    itemsList.Items.Add(outputName);
                }));
            }

            foreach (var item in addedFiles)
            {
                var code2 = File.ReadAllText(Path.Combine(proj2Path, item));

                code2 = PreprocessCode(code2);

                var diff = GetDiff(item, "", code2);

                if (diff.Length == 0)
                    continue;

                var outputFolder = Path.Combine(outPath, Path.GetDirectoryName(item));
                if (!Directory.Exists(outputFolder))
                    Directory.CreateDirectory(outputFolder);

                var outputFile = Path.Combine(outputFolder, Path.GetFileName(item) + ".html");

                var outputName = outputFile.Substring(outPath.Length + 1);
                outputName = outputName.Substring(0, outputName.Length - 5);

                File.WriteAllText(outputFile, diff);

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    itemsList.Items.Add(outputName);
                }));
            }
        }

        private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadSelectedItem();
        }

        private void LoadSelectedItem()
        {
            if (itemsList.SelectedItem == null)
                return;

            var path = itemsList.SelectedItem.ToString();

            var readHtml = File.ReadAllText(Path.Combine(outputHtmlPath, path + ".html"));
            webBrowser.NavigateToString(readHtml);
        }

        private void WebBrowser_LoadCompleted(object sender, NavigationEventArgs e)
        {
            if (showDiffOnlyCheckBox.IsChecked == false)
                webBrowser.InvokeScript("eval", "showAll();");

            if (darkModeCheckBox.IsChecked == true)
                webBrowser.InvokeScript("eval", "enableDarkMode();");
        }

        private void OriginalApkBrowse_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog()
            {
                CheckFileExists = true,
                CheckPathExists = true,
                Filter = "APK Files (*.apk)|*.apk|All Files|*",
            };

            if (openFileDialog.ShowDialog() == true)
            {
                originalApkText.Text = openFileDialog.FileName;
            }
        }

        private void ModifiedApkBrowse_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog()
            {
                CheckFileExists = true,
                CheckPathExists = true,
                Filter = "APK Files (*.apk)|*.apk|All Files|*",
            };

            if (openFileDialog.ShowDialog() == true)
            {
                modifiedApkText.Text = openFileDialog.FileName;
            }
        }

        private async void BeginDecompile_Click(object sender, RoutedEventArgs e)
        {
            selectView.Visibility = Visibility.Collapsed;
            resultsView.Visibility = Visibility.Visible;

            var origApk = originalApkText.Text;
            var modApk = modifiedApkText.Text;

            await Task.Run(() =>
            {
                BeginProcess(origApk, modApk);
            });
        }

        private void SetStatusText(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                statusText.Text = text;
            }));
        }

        private void BeginProcess(string origApk, string modApk)
        {
            string tempLocation = InitTempDirectory();

            var origApkExtractPath = Path.Combine(tempLocation, "origApk");
            var modApkExtractPath = Path.Combine(tempLocation, "modApk");

            var origApkDecompilePath = Path.Combine(tempLocation, "origProj");
            var modApkDecompilePath = Path.Combine(tempLocation, "modProj");

            var outputPath = Path.Combine(tempLocation, "output");

            SetStatusText($"Extracting original apk file... ({origApk})");
            ExtractApk(origApk, origApkExtractPath);

            var origAssemblyFiles = Directory.GetFiles(origApkExtractPath, "Assembly-CSharp.dll", SearchOption.AllDirectories);
            if (origAssemblyFiles.Length != 1)
            {
                SetStatusText($"*** FOUND {origAssemblyFiles.Length} files called Assembly-CSharp.dll in original apk! Aborted.");
                return;
            }
            var origAssembly = origAssemblyFiles[0];


            SetStatusText($"Extracting modified apk file... ({modApk})");
            ExtractApk(modApk, modApkExtractPath);

            var modAssemblyFiles = Directory.GetFiles(modApkExtractPath, "Assembly-CSharp.dll", SearchOption.AllDirectories);
            if (modAssemblyFiles.Length != 1)
            {
                SetStatusText($"*** FOUND {modAssemblyFiles.Length} files called Assembly-CSharp.dll in modified apk! Aborted.");
                return;
            }
            var modAssembly = modAssemblyFiles[0];


            SetStatusText("Decompiling original apk...");
            DecompileAsProject(origAssembly, origApkDecompilePath, new string[] { });

            SetStatusText("Decompiling modified apk...");
            DecompileAsProject(modAssembly, modApkDecompilePath, new string[] { });

            SetStatusText("Finding differences...");
            GenerateDiffForProjects(origApkDecompilePath, modApkDecompilePath, outputPath);

            SetStatusText("Done.");
        }

        private void ShowDiffOnlyCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            LoadSelectedItem();
        }

        private void DarkModeCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            LoadSelectedItem();
        }
    }
}
