using System;
using System.ComponentModel.Design;
using System.Globalization;
using System.IO;
using System.Linq;

using EnvDTE;
using EnvDTE80;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using Task = System.Threading.Tasks.Task;

namespace TinyOS
{
    /// <summary>
    /// Command handler
    /// </summary>
    internal sealed class DeployCommand
    {
        /// <summary>
        /// Command ID.
        /// </summary>
        public const int CommandId = 0x0100;

        /// <summary>
        /// Command menu group (command set GUID).
        /// </summary>
        public static readonly Guid CommandSet = new Guid("120c2691-7853-4dfa-acc7-5baa6e2dfac4");

        /// <summary>
        /// VS Package that provides this command, not null.
        /// </summary>
        private readonly AsyncPackage package;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeployCommand"/> class.
        /// Adds our command handlers for menu (commands must exist in the command table file)
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        /// <param name="commandService">Command service to add command to, not null.</param>
        private DeployCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        /// <summary>
        /// Gets the instance of the command.
        /// </summary>
        public static DeployCommand Instance
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the service provider from the owner package.
        /// </summary>
        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider
        {
            get
            {
                return this.package;
            }
        }

        /// <summary>
        /// Initializes the singleton instance of the command.
        /// </summary>
        /// <param name="package">Owner package, not null.</param>
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // Switch to the main thread - the call to AddCommand in DeployCommand's constructor requires
            // the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new DeployCommand(package, commandService);
        }

        private readonly string LaunchFileName = "launch.json";

        public string WaitForProject { get; private set; }
        public string ProjectLaunchFilePath { get; private set; }
        public bool RemoteDebugLaunchOnDone { get; private set; }

        public static BuildEvents BuildEvents { get; set; }

        /// <summary>
        /// This function is the callback used to execute the command when the menu item is clicked.
        /// See the constructor to see how the menu item is associated with this function using
        /// OleMenuCommandService service and MenuCommand class.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event args.</param>
        private void Execute(object sender, EventArgs e)
        {
            //ThreadHelper.ThrowIfNotOnUIThread();
            //string message = string.Format(CultureInfo.CurrentCulture, "Inside {0}.MenuItemCallback()", this.GetType().FullName);
            //string title = "DeployCommand";

            //// Show a message box to prove we were here
            //VsShellUtilities.ShowMessageBox(
            //    this.package,
            //    message,
            //    title,
            //    OLEMSGICON.OLEMSGICON_INFO,
            //    OLEMSGBUTTON.OLEMSGBUTTON_OK,
            //    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            ThreadHelper.ThrowIfNotOnUIThread();
            string title = "DeployCommand";

            var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));

            var project = ((object[])dte.ActiveSolutionProjects).FirstOrDefault() as Project;

            if (project == null)
            {
                var msg = "No current project found.";
                //Logger.Log(msg);
                VsShellUtilities.ShowMessageBox(
                    package,
                    msg,
                    title,
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var projectFilePath = project.FullName;
            var solutionFilePath = dte.Solution.FullName;
            var searchPaths = new string[] { solutionFilePath, projectFilePath };
            ProjectLaunchFilePath = null;
            foreach (var path in searchPaths)
            {
                var dir = Path.GetDirectoryName(path);
                var fullPath = Path.Combine(dir, LaunchFileName);
                if (File.Exists(fullPath))
                {
                    ProjectLaunchFilePath = fullPath;
                    break;
                }
            }

            if (ProjectLaunchFilePath == null)
            {
                var msg = $"No file {LaunchFileName} found." +
                    $" Search paths: {string.Join(", ", searchPaths.Select(s => Path.GetDirectoryName(s)))}";
                //Logger.Log(msg);
                VsShellUtilities.ShowMessageBox(
                    package,
                    msg,
                    title,
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            if (BuildEvents == null)
            {
                BuildEvents = dte.Events.BuildEvents;
                BuildEvents.OnBuildDone += this.BuildEvents_OnBuildDone;
                BuildEvents.OnBuildProjConfigDone += this.BuildEvents_OnBuildProjConfigDone;
            }

            dte.SuppressUI = false;
            var configurationName = project.ConfigurationManager.ActiveConfiguration.ConfigurationName;
            var projectName = project.UniqueName;
            WaitForProject = projectName;
            //Logger.Log($"Building project {projectName} in configuration {configurationName}");
            dte.Solution.SolutionBuild.BuildProject(configurationName, projectName);
        }
        private void BuildEvents_OnBuildProjConfigDone(string project, string projectConfig, string platform, string solutionConfig, bool success)
        {
            if (project == WaitForProject && success)
            {
                RemoteDebugLaunchOnDone = true;
            }
            else
            {
                RemoteDebugLaunchOnDone = false;
            }
        }

        private void BuildEvents_OnBuildDone(vsBuildScope scope, vsBuildAction action)
        {
            if (RemoteDebugLaunchOnDone)
            {
                //Logger.Log($"Project {WaitForProject} was built successfully. Invoking remote debug command");
                var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
                dte.ExecuteCommand("DebugAdapterHost.Launch", $"/LaunchJson:\"{ProjectLaunchFilePath}\"");
            }

            RemoteDebugLaunchOnDone = false;
            WaitForProject = null;
        }
    }
}
