using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace InventorRayOptics
{
    /// <summary>Small modal WinForms dialogs for the handful of native prompts the Ray
    /// Optics ribbon buttons need (naming a save, picking one of several existing items)
    /// — deliberately minimal rather than pulling in a bigger dialog framework.</summary>
    internal static class PromptDialogs
    {
        /// <summary>Free-text name entry, with existing names offered as a dropdown for
        /// easy overwrite. Returns null if the user cancels or enters a blank name.</summary>
        public static string PromptForName(string title, string message, IEnumerable<string> existingNames)
        {
            using (var form = new Form
            {
                Text = title, Width = 380, Height = 160,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false, MaximizeBox = false,
            })
            {
                var label = new Label { Text = message, AutoSize = true, Location = new Point(12, 12) };
                var combo = new ComboBox
                {
                    Location = new Point(12, 36), Width = 340,
                    DropDownStyle = ComboBoxStyle.DropDown,
                };
                foreach (var name in existingNames) combo.Items.Add(name);

                var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Location = new Point(196, 80) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(277, 80) };
                form.Controls.Add(label);
                form.Controls.Add(combo);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                var result = form.ShowDialog();
                var text = combo.Text?.Trim();
                return result == DialogResult.OK && !string.IsNullOrEmpty(text) ? text : null;
            }
        }

        /// <summary>Pick one entry from a fixed list. Returns null if the user cancels.</summary>
        public static string PromptForChoice(string title, string message, IEnumerable<string> options)
        {
            using (var form = new Form
            {
                Text = title, Width = 380, Height = 320,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterScreen,
                MinimizeBox = false, MaximizeBox = false,
            })
            {
                var label = new Label { Text = message, AutoSize = true, Location = new Point(12, 12) };
                var list = new ListBox { Location = new Point(12, 36), Width = 340, Height = 200 };
                foreach (var option in options) list.Items.Add(option);
                if (list.Items.Count > 0) list.SelectedIndex = 0;

                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(196, 244) };
                var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(277, 244) };
                form.Controls.Add(label);
                form.Controls.Add(list);
                form.Controls.Add(ok);
                form.Controls.Add(cancel);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                var result = form.ShowDialog();
                return result == DialogResult.OK ? list.SelectedItem as string : null;
            }
        }
    }
}
