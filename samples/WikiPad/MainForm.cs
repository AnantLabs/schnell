#region License, Terms and Author(s)
//
// Schnell - Wiki widgets
// Copyright (c) 2007 Atif Aziz. All rights reserved.
//
//  Author(s):
//      Atif Aziz, http://www.raboof.com
//
// This library is free software; you can redistribute it and/or modify it 
// under the terms of the GNU Lesser General Public License as published by 
// the Free Software Foundation; either version 2.1 of the License, or (at 
// your option) any later version.
//
// This library is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or 
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Lesser General Public 
// License for more details.
//
// You should have received a copy of the GNU Lesser General Public License
// along with this library; if not, write to the Free Software Foundation, 
// Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA 
//
#endregion

namespace WikiPad
{
    #region Imports

    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Drawing;
    using System.IO;
    using System.Net;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Web.UI;
    using System.Windows.Forms;
    using Microsoft.VisualBasic;
    using Properties;
    using Schnell;
    using System.ComponentModel.Design;
    
    using Control = System.Windows.Forms.Control;

    #endregion

    public sealed partial class MainForm : Form
    {
        private DateTime _lastChangeTime;
        private bool _wikiChanged;
        private string _documentPath;
        private bool _dirty;
        private readonly string _title;
        private bool _exiting;
        private readonly string _newLine;
        private Uri _lastImportedUrl;
        private readonly Dictionary<Type, ICommandContainer> _commandsByType;
        private readonly ControlFocusWatchdog _focusWatchdog;
        private ReplaceForm replaceForm;
        
        private static readonly Regex _tagExpression = new Regex(@"\</?[a-z]+(.*?)\>", 
            RegexOptions.IgnoreCase
            | RegexOptions.Singleline 
            | RegexOptions.Compiled 
            | RegexOptions.CultureInvariant);

        private const string _defaultFileName = "Wiki1.wiki";

        public MainForm()
        {
            InitializeComponent();

            _saveFileDialog.Filter = _openFileDialog.Filter;
            _title = Text;

            NamedCommandTable commands = new NamedCommandTable();
            commands.FillStandardCommands();
            _commandProvider.CommandTable = commands;

            CommandContainer cc = new CommandContainer();

            cc.Add(new Command<TextBoxBase>(StandardCommands.Undo,
                delegate(TextBoxBase control) { control.Undo(); },
                delegate(TextBoxBase control) { return !control.ReadOnly && control.CanUndo; }));
            
            cc.Add(new Command<TextBoxBase>(StandardCommands.Copy, 
                delegate(TextBoxBase control) { control.Copy(); }, 
                delegate(TextBoxBase control) { return control.SelectionLength > 0; }));
            cc.Add(new Command<TextBoxBase>(StandardCommands.Cut, 
                delegate(TextBoxBase control) { control.Cut(); },
                delegate(TextBoxBase control) { return !control.ReadOnly && control.SelectionLength > 0; }));
            cc.Add(new Command<TextBoxBase>(StandardCommands.Paste,
                delegate(TextBoxBase control) { control.Paste(); },
                delegate(TextBoxBase control) { return !control.ReadOnly && Clipboard.ContainsText(); }));
            
            cc.Add(new Command<TextBoxBase>(StandardCommands.SelectAll,
                delegate(TextBoxBase control) { control.SelectAll(); }));

            _commandsByType = new Dictionary<Type, ICommandContainer>();
            _commandsByType.Add(typeof(TextBoxBase), cc);

            _focusWatchdog = new ControlFocusWatchdog();
            _focusWatchdog.Watch(_wikiBox, _htmlBox);

            switch (Settings.Default.NewLine.ToLowerInvariant())
            {
                case "mac":
                case "cr": _newLine = "\r"; break;
                case "windows":
                case "win":
                case "crlf": _newLine = "\r\n"; break;
                case "unix":
                case "lf": _newLine = "\n"; break;
                default: _newLine = Environment.NewLine; break;
            }

            _webBrowser.DocumentCompleted += delegate { _webBrowser.AllowNavigation = false; };
        }

        public string Title
        {
            get { return _title; }
        }

        private string DocumentPath
        {
            get { return _documentPath ?? string.Empty; }

            set
            {
                _documentPath = value;
                UpdateTitle();
            }
        }

        public bool Dirty
        {
            get { return _dirty; }

            set
            {
                _dirty = value;
                UpdateTitle();
            }
        }

        private void Reformat() 
        {
            //
            // Write out the HTML document lead.
            //

            StringWriter sw = new StringWriter();
            XhtmlTextWriter htmlWriter = new XhtmlTextWriter(sw, "  ");
            htmlWriter.WriteLine("<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Transitional//EN\" \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-transitional.dtd\">");
            htmlWriter.AddAttribute("xmlns", "http://www.w3.org/1999/xhtml");
            htmlWriter.RenderBeginTag(HtmlTextWriterTag.Html);
            htmlWriter.RenderBeginTag(HtmlTextWriterTag.Head);
            htmlWriter.RenderBeginTag(HtmlTextWriterTag.Title);
            htmlWriter.WriteEncodedText(Path.GetFileNameWithoutExtension(Mask.EmptyString(DocumentPath, _defaultFileName)));
            htmlWriter.RenderEndTag();
            htmlWriter.AddAttribute(HtmlTextWriterAttribute.Type, "text/css");
            htmlWriter.RenderBeginTag(HtmlTextWriterTag.Style);
            htmlWriter.Write(@"a:link{ color:#00c}
      body{ background:#fff;  font-size:83%;  font-family:arial,sans-serif}
      td, th{ font-size:100%}
      td{ border:solid 1px #aaa;  padding:5px}
      table{ border-collapse:separate}
      h1{ font-size:x-large;  margin-top:0px}
      h2{ font-size:large}
      h3{ font-size:medium}
      h4{ font-size:small}
      img{ border:0}
      pre{ margin-left:2em;  padding:0.5em;  border-left:3px solid #ccc}");
            htmlWriter.RenderEndTag(/* style */); 
            htmlWriter.RenderEndTag(/* head */);
            htmlWriter.RenderBeginTag(HtmlTextWriterTag.Body);

            //
            // Format the wiki into the HTML body.
            //

            new HtmlFormatter().Format(new WikiParser().Parse(new StringReader(_wikiBox.Text)), htmlWriter);

            //
            // Conclude the HTML document.
            //

            htmlWriter.RenderEndTag(/* body */);
            htmlWriter.RenderEndTag(/* html */);
            
            string html = sw.GetStringBuilder().ToString().Replace("\r", string.Empty);

            //
            // Update the WebBrowser with the HTML preview.
            //

            _webBrowser.AllowNavigation = true;
            _webBrowser.DocumentText = html;

            //
            // Update the RichTextBox with the HTML source and highlight 
            // the markup.
            //

            _htmlBox.Clear();
            _htmlBox.Text = html;
            HighlightMarkup(_htmlBox);

            //
            // The preview is now up to date with the wiki changes.
            //

            _wikiChanged = false;
        }

        private static void HighlightMarkup(RichTextBox rtb)
        {
            Debug.Assert(rtb != null);

            Font tagFont = new Font(rtb.Font.FontFamily, rtb.Font.Size, FontStyle.Regular);

            foreach (Match match in _tagExpression.Matches(rtb.Text))
            {
                rtb.SelectionStart = match.Index;
                rtb.SelectionLength = match.Length;
                rtb.SelectionColor = Color.Blue;
                rtb.SelectionFont = tagFont;
            }
        }

        public void OpenFile(string path)
        {
            string text;

            try
            {
                using (CurrentCursorScope.EnterWait())
                    text = File.ReadAllText(path);
            }
            catch (IOException e)
            {
                Program.ShowExceptionDialog(e, "Load Error", this);
                return;
            }

            Open(text, path);
        }

        private void Open(string text, string path)
        {
            _wikiBox.Text = NormalizeLineEndings(text);
            DocumentPath = path;
            Dirty = false;
            Reformat();
        }

        private static string NormalizeLineEndings(string str)
        {
            return NormalizeLineEndings(str, null);
        }

        private static string NormalizeLineEndings(string str, string lineEnding)
        {
            StringBuilder sb = new StringBuilder(str);

            return sb.Replace("\r\n", "\n") // Normalizes all line-endings via LF.
                     .Replace("\r", "\n")
                     .Replace("\n", lineEnding ?? Environment.NewLine).ToString();
        }

        private bool Save()
        {
            return Save(false);
        }

        private bool Save(bool prompt)
        {
            string path = DocumentPath;

            //
            // Prompt for a new path?
            //

            if (prompt || string.IsNullOrEmpty(path))
            {
                _saveFileDialog.FileName = Mask.EmptyString(path, _defaultFileName);

                if (_saveFileDialog.ShowDialog(this) != DialogResult.OK)
                    return false;

                path = _saveFileDialog.FileName;
            }

            //
            // Save the document!
            //

            try
            {
                using (CurrentCursorScope.EnterWait())
                    File.WriteAllText(path, _wikiBox.Text.Replace(Environment.NewLine, _newLine));
            }
            catch (IOException e)
            {
                //
                // Tell the use about the error.
                //

                Program.ShowExceptionDialog(e, "Save Error", this);
                return false;
            }

            //
            // Commit the new path.
            //

            DocumentPath = path;
            Dirty = false;

            return true;
        }

        private void UpdateTitle()
        {
            string fileName = Mask.EmptyString(Path.GetFileName(DocumentPath), _defaultFileName);
            StringBuilder sb = new StringBuilder(fileName.Length + Title.Length + 5);

            sb.Append(fileName);

            if (Dirty)
                sb.Append('*');

            sb.Append(" - ").Append(Title);

            Text = sb.ToString();
        }

        private bool EnsureUserSaved()
        {
            if (!Dirty)
                return true;

            string prompt = string.Format("Do you want save the changes to {0}?", Path.GetFileName(Mask.EmptyString(DocumentPath, _defaultFileName)));

            DialogResult reply = MessageBox.Show(this, prompt, "Save",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button1);

            if (reply == DialogResult.Cancel)
                return false;

            if (reply == DialogResult.No)
                return true;

            return Save();
        }

        //
        // Form events
        //

        private void Form_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_exiting && e.CloseReason != CloseReason.UserClosing)
                return;

            _exiting = false;

            if (!EnsureUserSaved())
                e.Cancel = true;
        }

        private void Form_Load(object sender, EventArgs e)
        {
            UpdateTitle();
        }

        private void Form_Activated(object sender, EventArgs e)
        {
            Activated -= Form_Activated;
            _wikiBox.Focus();
        }

        //
        // Control events
        //

        private void WikiBox_TextChanged(object sender, EventArgs e)
        {
            _lastChangeTime = DateTime.Now;
            _wikiChanged = true;
            Dirty = true;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            TimeSpan changeLapse = DateTime.Now - _lastChangeTime;

            if (!_wikiChanged || changeLapse <= TimeSpan.FromMilliseconds(500))
                return;

            Reformat();
        }

        //
        // File menus
        //

        private void FileNewMenu_Click(object sender, EventArgs e)
        {
            if (!EnsureUserSaved())
                return;

            Open(string.Empty, null);
        }

        private void FileOpenMenu_Click(object sender, EventArgs args)
        {
            if (!EnsureUserSaved())
                return;

            if (_openFileDialog.ShowDialog(this) != DialogResult.OK)
                return;

            OpenFile(_openFileDialog.FileName);
        }

        private void FileSaveMenu_Click(object sender, EventArgs e)
        {
            Save();
        }

        private void FileSaveAsMenu_Click(object sender, EventArgs e)
        {
            Save(true);
        }

        private void ImportFromWebMenu_Click(object sender, EventArgs args)
        {
            if (!EnsureUserSaved())
                return;

            string input = Interaction.InputBox("Enter wiki URL:", "Import From Web",
                _lastImportedUrl != null ? 
                    _lastImportedUrl.ToString() : "http://support.googlecode.com/svn/wiki/WikiSyntax.wiki",
                (int)(Location.X + (Size.Height / 10f)),
                (int)(Location.Y + (Size.Height / 10f)));

            if (string.IsNullOrEmpty(input))
                return;

            Uri url;

            try
            {
                url = new Uri(input, UriKind.Absolute);
            }
            catch (UriFormatException e)
            {
                Program.ShowExceptionDialog(e, "Import URL Error", this);
                return;
            }

            string text;

            try
            {
                using (CurrentCursorScope.EnterWait())
                {
                    WebClient webClient = new WebClient();
                    webClient.Credentials = CredentialCache.DefaultCredentials;
                    text = webClient.DownloadString(url);
                }
            }
            catch (WebException e)
            {
                Program.ShowExceptionDialog(e, "Import Error", this);
                return;
            }

            _lastImportedUrl = url;
            Open(text, null);
            Dirty = true;
        }

        private void FileExitMenu_Click(object sender, EventArgs e)
        {
            _exiting = true;
            Close();
        }

        //
        // Edit menus
        //

        private void EditMenu_DropDownOpening(object sender, EventArgs e)
        {
            foreach (ToolStripItem item in _editMenu.DropDownItems)
            {
                NamedCommand id = _commandProvider.CommandTable.FindByName(_commandProvider.GetCommand(item));

                if (id != null)
                {
                    ICommand command = FindCommand(id);
                    item.Enabled = command != null && command.Enabled;
                }
            }
        }

        private void StandardCommand_CommandClick(object sender, StandardCommandEventArgs e)
        {
            ICommand command = FindCommand(e.Command);
            if (command != null && command.Enabled)
                command.Execute();
        }

        private ICommandContainer FindCommandContainer(Control control)
        {
            Debug.Assert(control != null);

            for (Type controlType = control.GetType();
                 controlType != typeof(Control); controlType = controlType.BaseType)
            {
                ICommandContainer container;
                if (_commandsByType.TryGetValue(controlType, out container))
                    return container;
            }

            return null;
        }

        private ICommand FindCommand(CommandID id)
        {
            Debug.Assert(id != null);

            return FindCommand(id, _focusWatchdog.ActiveControl);
        }

        private ICommand FindCommand(CommandID id, Control target) 
        {
            if (target == null)
                return null;

            ICommandContainer container = FindCommandContainer(target);
            if (container == null)
                return null;

            ICommand command = container.FindCommand(id);
            if (command == null)
                return null;

            return command.Bind(target);
        }

        private void findToolStripMenuItem_Click(object sender, EventArgs e) 
        {
            FindForm findForm = new FindForm(_wikiBox);
            findForm.Show(this);
        }

        private void replaceToolStripMenuItem_Click(object sender, EventArgs e) 
        {
            if (replaceForm == null)
                replaceForm = new ReplaceForm(_wikiBox);
            replaceForm.Show(this);
        }

        //
        // Help menus
        //

        private void HelpWikiSyntaxMenu_Click(object sender, EventArgs e)
        {
            Process.Start("http://code.google.com/p/support/wiki/WikiSyntax");
        }




    }
}