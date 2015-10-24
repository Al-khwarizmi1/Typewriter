﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using EnvDTE;
using Typewriter.CodeModel.Configuration;
using Typewriter.Configuration;
using Typewriter.Generation.Controllers;
using Typewriter.VisualStudio;
using File = Typewriter.CodeModel.File;

namespace Typewriter.Generation
{
    public class Template
    {
        private readonly object _locker = new object();

        private readonly List<Type> _customExtensions = new List<Type>();
        private readonly string _template;
        private readonly string _templatePath;
        private readonly string _projectPath;
        private readonly ProjectItem _projectItem;
        private readonly SettingsImpl _configuration;

        public Template(ProjectItem projectItem)
        {
            var stopwatch = Stopwatch.StartNew();

            _projectItem = projectItem;
            _templatePath = projectItem.Path();
            _projectPath = Path.GetDirectoryName(projectItem.ContainingProject.FullName);

            var code = System.IO.File.ReadAllText(_templatePath);
            _template = TemplateCodeParser.Parse(_projectItem, code, _customExtensions);

            _configuration = new SettingsImpl(_projectItem);

            var templateClass = _customExtensions.FirstOrDefault();
            if (templateClass?.GetConstructor(new []{typeof(Settings)}) != null)
            {
                Activator.CreateInstance(templateClass, _configuration);
            }

            stopwatch.Stop();
            Log.Debug("Template ctor {0} ms", stopwatch.ElapsedMilliseconds);
        }

        public ICollection<string> GetFilesToRender()
        {
            return ProjectHelpers.GetProjectItems(_projectItem.DTE, _configuration.IncludedProjects, "*.cs").ToList();
        }

        public bool ShouldRenderFile(string filename)
        {
            return ProjectHelpers.ProjectListContainsItem(_projectItem.DTE, filename, _configuration.IncludedProjects);
        }

        public string Render(File file, out bool success)
        {
            return Parser.Parse(_projectItem, file.FullName, _template, _customExtensions, file, out success);
        }

        public bool RenderFile(File file, bool saveProjectFile)
        {
            bool success;
            var output = Render(file, out success);

            if (success)
            {
                if (output == null)
                {
                    DeleteFile(file.FullName, saveProjectFile);
                }
                else
                {
                    SaveFile(file, output, saveProjectFile);
                }
            }

            return success;
        }

        private void SaveFile(File file, string output, bool saveProjectFile)
        {
            lock (_locker)
            {
                ProjectItem item;
                var outputPath = GetOutputPath(file);

                if (HasChanged(outputPath, output))
                {
                    CheckOutFileFromSourceControl(outputPath);

                    System.IO.File.WriteAllText(outputPath, output);
                    item = FindProjectItem(outputPath) ?? _projectItem.ProjectItems.AddFromFile(outputPath);
                }
                else
                {
                    item = FindProjectItem(outputPath);
                }

                SetMappedSourceFile(item, file.FullName);

                if (saveProjectFile)
                    _projectItem.ContainingProject.Save();
            }
        }

        public void DeleteFile(string path, bool saveProjectFile)
        {
            lock (_locker)
            {
                var item = GetExistingItem(path);

                if (item != null)
                {
                    item.Delete();

                    if (saveProjectFile)
                        _projectItem.ContainingProject.Save();
                }
            }
        }

        public void RenameFile(File file, string oldPath, string newPath, bool saveProjectFile)
        {
            lock (_locker)
            {
                var item = GetExistingItem(oldPath);

                if (item != null)
                {
                    if (Path.GetFileName(oldPath)?.Equals(Path.GetFileName(newPath)) ?? false)
                    {
                        SetMappedSourceFile(item, newPath);

                        if (saveProjectFile)
                            _projectItem.ContainingProject.Save();

                        return;
                    }

                    var newOutputPath = GetOutputPath(file);

                    item.Name = Path.GetFileName(newOutputPath);
                    SetMappedSourceFile(item, newPath);

                    if (saveProjectFile)
                        _projectItem.ContainingProject.Save();
                }
            }
        }

        private string GetMappedSourceFile(ProjectItem item)
        {
            if (item == null) return null;

            var value = item.Properties.Item("CustomToolNamespace").Value as string;
            var path = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(Path.Combine(_projectPath, value));

            return path;
        }

        private void SetMappedSourceFile(ProjectItem item, string path)
        {
            if (_projectItem == null) throw new ArgumentException("item");
            if (path == null) throw new ArgumentException("path");

            var pathUri = new Uri(path);
            var folderUri = new Uri(_projectPath.Trim(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            var relativeSourcePath = Uri.UnescapeDataString(folderUri.MakeRelativeUri(pathUri).ToString().Replace('/', Path.DirectorySeparatorChar));

            if (relativeSourcePath.Equals(GetMappedSourceFile(item), StringComparison.InvariantCultureIgnoreCase) == false)
            {
                var property = item.Properties.Item("CustomToolNamespace");
                if (property == null) throw new InvalidOperationException("Cannot find CustomToolNamespace property");

                property.Value = relativeSourcePath;
            }
        }

        private ProjectItem GetExistingItem(string path)
        {
            foreach (ProjectItem item in _projectItem.ProjectItems)
            {
                try
                {
                    if (path.Equals(GetMappedSourceFile(item), StringComparison.InvariantCultureIgnoreCase))
                    {
                        return item;
                    }
                }
                catch
                {
                    // Can't read properties from project item sometimes when deleting miltiple files
                }
            }

            return null;
        }

        private string GetOutputPath(File file)
        {
            var path = file.FullName;
            var directory = Path.GetDirectoryName(_templatePath);
            var filename = GetOutputFilename(file, path);
            var outputPath = Path.Combine(directory, filename);

            for (var i = 1; i < 1000; i++)
            {
                var item = FindProjectItem(outputPath);
                if (item == null) return outputPath;

                var mappedSourceFile = GetMappedSourceFile(item);
                if (mappedSourceFile == null || path.Equals(mappedSourceFile, StringComparison.InvariantCultureIgnoreCase)) return outputPath;

                var name = filename.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) ?
                    filename.Substring(0, filename.Length - 5) :
                    filename.Substring(0, filename.LastIndexOf(".", StringComparison.Ordinal));

                var extension = filename.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase) ? 
                    ".d.ts" : 
                    filename.Substring(filename.LastIndexOf(".", StringComparison.Ordinal));

                outputPath = Path.Combine(directory, $"{name} ({i}){extension}");
            }

            throw new Exception("GetOutputPath");
        }

        private string GetOutputFilename(File file, string sourcePath)
        {
            var sourceFilename = Path.GetFileNameWithoutExtension(sourcePath);
            var extension = GetOutputExtension();

            try
            {
                if (_configuration.OutputFilenameFactory != null)
                {
                    var filename = _configuration.OutputFilenameFactory(file);

                    if (filename.Contains(".") == false)
                        filename += extension;

                    return filename;
                }
            }
            catch (Exception exception)
            {
                Log.Warn($"Can't get output filename for '{sourcePath}' ({exception.Message})");
            }

            return sourceFilename + extension;
        }

        private string GetOutputExtension()
        {
            var extension = _configuration.OutputExtension;

            if (string.IsNullOrWhiteSpace(extension))
                return ".ts";

            return "." + extension.Trim('.');
        }

        private static bool HasChanged(string path, string output)
        {
            if (System.IO.File.Exists(path))
            {
                var current = System.IO.File.ReadAllText(path);
                if (current == output)
                {
                    return false;
                }
            }

            return true;
        }

        private ProjectItem FindProjectItem(string path)
        {
            foreach (ProjectItem item in _projectItem.ProjectItems)
            {
                try
                {
                    var itemPath = item.Path();
                    if (itemPath.Equals(path, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return item;
                    }
                }
                catch
                {
                    // Can't read properties from project item sometimes when deleting miltiple files
                }
            }

            return null;
        }

        private void CheckOutFileFromSourceControl(string path)
        {
            try
            {
                var dte = _projectItem.DTE;
                var fileExists = System.IO.File.Exists(path) && dte.Solution.FindProjectItem(path) != null;
                var isUnderScc = dte.SourceControl.IsItemUnderSCC(path);
                var isCheckedOut = dte.SourceControl.IsItemCheckedOut(path);

                if (fileExists && isUnderScc && isCheckedOut == false)
                {
                    dte.SourceControl.CheckOutItem(path);
                }
            }
            catch (Exception exception)
            {
                Log.Error(exception.Message);
            }
        }

        public void VerifyProjectItem()
        {
            // ReSharper disable once UnusedVariable
            var dummy = _projectItem.FileNames[1];
        }
    }
}
