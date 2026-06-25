using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Fabrication;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using HangerLayout.Models;
using HangerLayout.Revit;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace HangerLayout.UI
{
    /// <summary>
    /// Boolean → Visibility converter. Set Inverse=True to flip the mapping.
    /// Used to swap a ComboBox with a "—" placeholder depending on whether
    /// the target set contains parts of a given category.
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public bool Inverse { get; set; }
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            bool b = value is bool v && v;
            if (Inverse) b = !b;
            return b ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    public partial class HangerLayoutDialog : Window
    {
        private readonly UIDocument _uiDoc;
        public HangerLayoutViewModel ViewModel { get; }

        public HangerLayoutDialog(UIDocument uiDoc, List<SupportSpec> initialSpecs, List<string> services)
        {
            InitializeComponent();
            _uiDoc = uiDoc;
            ViewModel = new HangerLayoutViewModel(initialSpecs, services);
            DataContext = ViewModel;

            // Load persisted placement settings (project-wide).
            try
            {
                var ps = HangerSettingsStore.GetPlacementSettings(uiDoc.Document);
                ViewModel.MinSpacingEnabled = ps.MinSpacingEnabled;
                ViewModel.MinSpacingInches  = ps.MinSpacingInches;
                ViewModel.UseMechEqAsStart  = ps.UseMechEqAsStart;
            }
            catch { /* defaults stand */ }

            // Auto-refresh target categories whenever the user changes
            // mode / service / scope, or after the dialog gets focus
            // (lets us pick up external Revit selection changes).
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(HangerLayoutViewModel.IsModeCurrent)
                    || e.PropertyName == nameof(HangerLayoutViewModel.IsModePick)
                    || e.PropertyName == nameof(HangerLayoutViewModel.IsModeService)
                    || e.PropertyName == nameof(HangerLayoutViewModel.SelectedServiceName)
                    || e.PropertyName == nameof(HangerLayoutViewModel.IsScopeProject)
                    || e.PropertyName == nameof(HangerLayoutViewModel.IsScopeView))
                {
                    RefreshTargetCategories();
                }
            };
            Loaded   += (s, e) => RefreshTargetCategories();
            Activated += (s, e) => RefreshTargetCategories();

            // Unsaved-changes guard: triggered for both the Close button (via
            // Window.Close()) AND the title-bar X. Apply's implicit save does
            // NOT clear the dirty flag — only an explicit "Save Specs" does.
            Closing += OnDialogClosing;
        }

        private bool _suppressClosingPrompt;

        private void OnDialogClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_suppressClosingPrompt) return;
            if (!ViewModel.IsSpecsDirty) return;

            var result = MessageBox.Show(
                this,
                "You have unsaved changes to Hanger Settings.\n\nSave before closing?",
                "Unsaved changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                // Queue the save (it runs asynchronously on the API thread)
                // and let the close proceed. The handler captures a snapshot
                // of the specs up front, so the dialog can disappear before
                // the save commits without any data loss.
                SaveSpecs_Click(this, new RoutedEventArgs());
                _suppressClosingPrompt = true;
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
            }
            // No → just close, abandoning in-memory changes.
        }

        // ── Target-category detection (drives Pipe/Duct dropdown visibility) ─

        private void RefreshTargets_Click(object sender, RoutedEventArgs e)
            => RefreshTargetCategories();

        private void RefreshTargetCategories()
        {
            var mode  = ViewModel.GetMode();
            var scope = ViewModel.IsScopeView
                ? HangerServiceScope.ActiveView : HangerServiceScope.WholeProject;
            var service     = ViewModel.SelectedServiceName;
            var prePicked   = ViewModel.PickedElementIds.ToList();

            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                bool hasPipe = false, hasRoundDuct = false, hasRectDuct = false;
                try
                {
                    var doc   = uiApp.ActiveUIDocument.Document;
                    var uiDoc = uiApp.ActiveUIDocument;
                    var parts = CollectParts(doc, uiDoc, mode, scope, service, prePicked);
                    foreach (var fp in parts)
                    {
                        long cv = fp.Category?.Id.Value ?? 0;
                        if (cv == (long)BuiltInCategory.OST_FabricationPipework)
                        {
                            hasPipe = true;
                        }
                        else if (cv == (long)BuiltInCategory.OST_FabricationDuctwork)
                        {
                            var shape = DetectDuctShape(fp);
                            if      (shape == DuctShape.Round)       hasRoundDuct = true;
                            else if (shape == DuctShape.Rectangular) hasRectDuct  = true;
                            else { hasRoundDuct = true; hasRectDuct = true; }
                        }
                        if (hasPipe && hasRoundDuct && hasRectDuct) break;
                    }
                }
                catch { /* leave all three false on error */ }
                Dispatcher.Invoke(() =>
                {
                    ViewModel.HasPipeTarget      = hasPipe;
                    ViewModel.HasRoundDuctTarget = hasRoundDuct;
                    ViewModel.HasRectDuctTarget  = hasRectDuct;
                });
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        /// <summary>Classify a duct's cross-section. Tries the catalog CID set
        /// first (fastest, deterministic per content) and falls back to the
        /// end connector's profile type. Returns DuctShape.Any when neither
        /// signal is conclusive — that pushes the duct to BOTH lists so it
        /// isn't dropped silently.</summary>
        private static DuctShape DetectDuctShape(FabricationPart fp)
        {
            try
            {
                int cid = fp.ItemCustomId;
                // Rectangular catalog CIDs (per project memory): 1, 35, 866, 924
                if (cid == 1 || cid == 35 || cid == 866 || cid == 924)
                    return DuctShape.Rectangular;
                // Round catalog CIDs: 40, 41
                if (cid == 40 || cid == 41) return DuctShape.Round;

                // Geometric fallback for CIDs we don't recognise.
                foreach (var c in ConnectorHelper.GetPhysicalConnectors(fp))
                {
                    if (c.ConnectorType != ConnectorType.End) continue;
                    if (c.Shape == ConnectorProfileType.Round)       return DuctShape.Round;
                    if (c.Shape == ConnectorProfileType.Rectangular) return DuctShape.Rectangular;
                }
            }
            catch { }
            return DuctShape.Any;
        }

        // ── Pipe spec list buttons ───────────────────────────────────────────

        private void PipeSpecAdd_Click(object sender, RoutedEventArgs e)
            => ViewModel.AddSpec(HangerDomain.Pipe);

        private void PipeSpecDuplicate_Click(object sender, RoutedEventArgs e)
            => ViewModel.DuplicateSelectedSpec(HangerDomain.Pipe);

        private void PipeSpecDelete_Click(object sender, RoutedEventArgs e)
            => ViewModel.DeleteSelectedSpec(HangerDomain.Pipe);

        private void PipeRowAdd_Click(object sender, RoutedEventArgs e)
            => ViewModel.AddRow(HangerDomain.Pipe);

        private void PipeRowDelete_Click(object sender, RoutedEventArgs e)
            => ViewModel.DeleteSelectedRow(HangerDomain.Pipe, GetSelectedRow(HangerDomain.Pipe));

        // ── Duct spec list buttons ───────────────────────────────────────────

        private void DuctSpecAdd_Click(object sender, RoutedEventArgs e)
            => ViewModel.AddSpec(HangerDomain.Duct);

        private void DuctSpecDuplicate_Click(object sender, RoutedEventArgs e)
            => ViewModel.DuplicateSelectedSpec(HangerDomain.Duct);

        private void DuctSpecDelete_Click(object sender, RoutedEventArgs e)
            => ViewModel.DeleteSelectedSpec(HangerDomain.Duct);

        private void DuctRowAdd_Click(object sender, RoutedEventArgs e)
            => ViewModel.AddRow(HangerDomain.Duct);

        private void DuctRowDelete_Click(object sender, RoutedEventArgs e)
            => ViewModel.DeleteSelectedRow(HangerDomain.Duct, GetSelectedRow(HangerDomain.Duct));

        private RowVm? GetSelectedRow(HangerDomain domain)
        {
            // The DataGrid's SelectedItem isn't bound. Walk the visual tree to find it.
            var grids = FindVisualChildren<DataGrid>(this);
            foreach (var grid in grids)
            {
                if (grid.ItemsSource is ObservableCollection<RowVm> rows &&
                    grid.SelectedItem is RowVm sel)
                {
                    // Make sure this DataGrid belongs to the requested domain
                    var spec = domain == HangerDomain.Pipe
                        ? ViewModel.SelectedPipeSpec
                        : ViewModel.SelectedDuctSpec;
                    if (spec != null && rows == spec.Rows)
                        return sel;
                }
            }
            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
            where T : System.Windows.DependencyObject
        {
            if (parent == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T tChild) yield return tChild;
                foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
            }
        }

        // ── Hanger override help popup ───────────────────────────────────────

        private void HangerSelectionHelp_Click(object sender, RoutedEventArgs e)
            => ShowHangerSelectionHelp();

        private void ShowHangerSelectionHelp()
        {
            var dlg = new System.Windows.Window
            {
                Title                 = "How the default hanger is picked",
                Width                 = 580,
                Height                = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(16, 14, 16, 14),
            };
            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text         = "When no Hanger override is set on a spec, the placer " +
                               "scans the host's fabrication service for the first " +
                               "non-excluded hanger button that's compatible with the " +
                               "host part's connector profile.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 12),
            });

            stack.Children.Add(new TextBlock
            {
                Text       = "Shape compatibility — three-step precedence",
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                Margin     = new Thickness(0, 0, 0, 6),
            });
            stack.Children.Add(BulletLine(
                "1.",
                "Button name contains ROUND or PIPE → treated as Round-compatible. " +
                "This wins even if a rect form-factor word is also present " +
                "(e.g. \"Trapeze Hanger Round\" still matches Round hosts)."));
            stack.Children.Add(BulletLine(
                "2.",
                "Otherwise, button name contains RECTANGULAR, BEARER, or TRAPEZE → " +
                "treated as Rect-only."));
            stack.Children.Add(BulletLine(
                "3.",
                "Everything else defaults to Round-compatible. Catches Clevis, Ring, " +
                "Strap, Half Strap, J-Hook, Loop, Gripple, generic \"Hanger\", and " +
                "vendor SKUs without enumerating every variant."));

            stack.Children.Add(new TextBlock
            {
                Text       = "By host type",
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                Margin     = new Thickness(0, 12, 0, 6),
            });
            stack.Children.Add(BulletLine("•", "Round duct or pipe → picks Round-compatible."));
            stack.Children.Add(BulletLine("•", "Rectangular duct → picks Rect-only."));
            stack.Children.Add(BulletLine("•", "Oval or unknown profile → no filter (any non-excluded hanger)."));

            stack.Children.Add(new TextBlock
            {
                Text         = "If you set a Hanger override (\"Pick…\"), that exact button " +
                               "is always used — the shape filter is bypassed entirely. " +
                               "The override lets you force a specific hanger for content " +
                               "where the name-keyword heuristic doesn't match what you want.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 12, 0, 12),
            });

            stack.Children.Add(new TextBlock
            {
                Text       = "Why name keywords, not catalog CIDs?",
                FontWeight = FontWeights.SemiBold,
                FontSize   = 13,
                Margin     = new Thickness(0, 0, 0, 6),
            });
            stack.Children.Add(new TextBlock
            {
                Text         = "Most hanger products in the Fab catalog share the same " +
                               "CID regardless of which duct profile they're compatible " +
                               "with, so CID-based identification doesn't work. Name " +
                               "keywords are the only reliable signal — and they're easy " +
                               "to extend if you have non-standard vendor naming.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 12),
            });

            var ok = new Button
            {
                Content             = "Close",
                Padding             = new Thickness(20, 6, 20, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault           = true,
                IsCancel            = true,
            };
            ok.Click += (s, ev) => dlg.DialogResult = true;
            stack.Children.Add(ok);

            scroller.Content = stack;
            dlg.Content      = scroller;
            dlg.ShowDialog();
        }

        private static UIElement BulletLine(string bullet, string text)
        {
            var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition
                { Width = new GridLength(1, GridUnitType.Star) });
            var b = new TextBlock { Text = bullet, FontWeight = FontWeights.SemiBold };
            System.Windows.Controls.Grid.SetColumn(b, 0);
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            System.Windows.Controls.Grid.SetColumn(t, 1);
            row.Children.Add(b);
            row.Children.Add(t);
            return row;
        }

        // ── Hanger override pickers ──────────────────────────────────────────

        private void PipeHangerOverridePick_Click(object sender, RoutedEventArgs e)
            => OpenOverridePicker(HangerDomain.Pipe);

        private void DuctHangerOverridePick_Click(object sender, RoutedEventArgs e)
            => OpenOverridePicker(HangerDomain.Duct);

        private void PipeHangerOverrideClear_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedPipeSpec != null)
                ViewModel.SelectedPipeSpec.HangerOverride = null;
        }

        private void DuctHangerOverrideClear_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.SelectedDuctSpec != null)
                ViewModel.SelectedDuctSpec.HangerOverride = null;
        }

        private void OpenOverridePicker(HangerDomain domain)
        {
            var spec = domain == HangerDomain.Pipe
                ? ViewModel.SelectedPipeSpec
                : ViewModel.SelectedDuctSpec;
            if (spec == null) return;

            // Snapshot hanger buttons on the Revit thread, then show modal child
            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var entries = EnumerateHangerButtons(doc);
                Dispatcher.Invoke(() =>
                {
                    Hide();
                    var dlg = new HangerButtonPickerDialog(entries) { Owner = this };
                    if (dlg.ShowDialog() == true && dlg.SelectedEntry != null)
                    {
                        spec.HangerOverride = dlg.SelectedEntry.OverrideKey;
                    }
                    Show();
                });
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        private static List<HangerButtonEntry> EnumerateHangerButtons(Document doc)
        {
            var result = new List<HangerButtonEntry>();
            try
            {
                var config = FabricationConfiguration.GetFabricationConfiguration(doc);
                if (config == null) return result;
                foreach (var svc in config.GetAllLoadedServices())
                {
                    for (int pi = 0; pi < svc.PaletteCount; pi++)
                    {
                        string groupName = string.Empty;
                        try { groupName = svc.GetPaletteName(pi) ?? string.Empty; } catch { }

                        for (int bi = 0; bi < svc.GetButtonCount(pi); bi++)
                        {
                            var btn = svc.GetButton(pi, bi);
                            if (btn == null) continue;
                            bool isHanger = false;
                            try { isHanger = btn.IsAHanger; } catch { }
                            if (!isHanger) continue;

                            result.Add(new HangerButtonEntry
                            {
                                ServiceName = svc.Name ?? string.Empty,
                                GroupName   = groupName,
                                ButtonName  = btn.Name ?? string.Empty,
                            });
                        }
                    }
                }
            }
            catch { }
            return result;
        }

        // ── Pick Elements (Hide → PickObjects → Show) ────────────────────────

        private void PickElements_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                List<ElementId> picked = new();
                try
                {
                    var uiDoc = uiApp.ActiveUIDocument;
                    var refs = uiDoc.Selection.PickObjects(
                        ObjectType.Element,
                        new FabricationPipeOrDuctFilter(),
                        "Pick fabrication pipes and ducts. Press Finish or Esc when done.");
                    picked = refs.Select(r => r.ElementId).ToList();
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                        ViewModel.StatusText = $"Pick failed: {ex.Message}");
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (picked.Count > 0)
                        {
                            ViewModel.PickedElementIds = picked;
                            ViewModel.StatusText = $"Picked {picked.Count} element(s).";
                            RefreshTargetCategories();
                        }
                        Show();
                    });
                }
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        // ── Refresh service dropdown ─────────────────────────────────────────

        // ── Pick Start element (defines flow direction for Before/After) ────

        private void PickStart_Click(object sender, RoutedEventArgs e)
        {
            Hide();
            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                ElementId? picked = null;
                int connIdx = 0;
                XYZ? chosenOrigin = null;
                string label = string.Empty;
                var uiDoc = uiApp.ActiveUIDocument;
                var preservedSelection = uiDoc.Selection.GetElementIds().ToList();
                try
                {
                    var doc = uiDoc.Document;
                    // Pick the pipe/duct directly. Returns BOTH the element AND
                    // the picked world point — no need to rely on Revit's snap
                    // settings (which were flaky for fabrication connector nodes).
                    var picker = new FabricationPipeOrDuctFilter();
                    var reference = uiDoc.Selection.PickObject(
                        Autodesk.Revit.UI.Selection.ObjectType.PointOnElement,
                        picker,
                        "Click near the END of a fabrication pipe / duct to mark it as the start of the run.");
                    if (reference == null) return;

                    var pickedElem = doc.GetElement(reference) as FabricationPart;
                    if (pickedElem == null) return;
                    XYZ snapPt = reference.GlobalPoint;

                    // Resolve to the picked element's nearest connector. Then
                    // (optional override) auto-pick its open end if there's
                    // exactly one — that's almost always the user's intent
                    // when picking a "start of run".
                    FabricationPart? bestPart = pickedElem;
                    int bestIdx = -1;
                    double bestDist = double.MaxValue;
                    {
                        var conns = ConnectorHelper.GetPhysicalConnectors(pickedElem);
                        for (int i = 0; i < conns.Count; i++)
                        {
                            double d = conns[i].Origin.DistanceTo(snapPt);
                            if (d < bestDist) { bestDist = d; bestIdx = i; }
                        }
                    }
                    if (bestPart != null && bestIdx >= 0)
                    {
                        // If the picked pipe has exactly one open connector
                        // (= a terminus pipe), prefer that as the start so
                        // flow propagates outward from the open end. This
                        // avoids the user having to click the open end
                        // exactly and matches the natural intent for
                        // "start of the run".
                        var bConns = ConnectorHelper.GetPhysicalConnectors(bestPart);
                        int openIdx = -1, openCount = 0;
                        for (int i = 0; i < bConns.Count; i++)
                        {
                            bool isConnected = false;
                            try { isConnected = bConns[i].IsConnected; } catch { }
                            if (!isConnected) { openIdx = i; openCount++; }
                        }
                        int chosenIdx = openCount == 1 ? openIdx : bestIdx;

                        picked  = bestPart.Id;
                        connIdx = chosenIdx;
                        chosenOrigin = bConns[chosenIdx].Origin;
                        string note = openCount == 1 && chosenIdx != bestIdx
                            ? "  [auto-picked open end]"
                            : "";
                        label   = $"id {bestPart.Id.Value} conn[{chosenIdx}] ({bestPart.ServiceName}){note}";
                    }
                }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => ViewModel.StatusText = $"Pick start failed: {ex.Message}");
                }
                finally
                {
                    try { uiDoc.Selection.SetElementIds(preservedSelection); } catch { }
                    Dispatcher.Invoke(() =>
                    {
                        if (picked != null) ViewModel.SetStartElement(picked, chosenOrigin, label);
                        Show();
                    });
                }
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        private void ClearStart_Click(object sender, RoutedEventArgs e)
            => ViewModel.SetStartElement(null, null, string.Empty);

        private void RefreshServices_Click(object sender, RoutedEventArgs e)
        {
            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                var svcs = CollectServiceNames(doc, ViewModel.IsScopeView);
                Dispatcher.Invoke(() => ViewModel.SetServiceNames(svcs));
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        private static List<string> CollectServiceNames(Document doc, bool viewScope)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collector = viewScope
                ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                : new FilteredElementCollector(doc);
            foreach (var fp in collector.OfClass(typeof(FabricationPart))
                                        .WhereElementIsNotElementType()
                                        .Cast<FabricationPart>())
            {
                if (!string.IsNullOrWhiteSpace(fp.ServiceName))
                    names.Add(fp.ServiceName);
            }
            return names.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ── Save specs ───────────────────────────────────────────────────────

        private void SaveSpecs_Click(object sender, RoutedEventArgs e)
        {
            var snapshot = ViewModel.SnapshotSpecs();
            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string? error = null;
                try
                {
                    using var tx = new Transaction(doc, "Hanger Layout — Save Specs");
                    tx.Start();
                    HangerSpecStore.Save(doc, snapshot);
                    tx.Commit();
                }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.Invoke(() =>
                {
                    ViewModel.StatusText = error == null
                        ? $"Saved {snapshot.Count} spec(s) to project."
                        : $"Save failed: {error}";
                    if (error == null) ViewModel.MarkSpecsClean();
                });
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        // ── EXPLORATORY: Dump SUPPORT.MAP for reverse-engineering ────────────
        // Auto-locates the file via the user's saved Pricing Source
        // DatabaseFolder; falls back to a file picker. Lets the user
        // optionally type a needle (spec name they set in Fab ESTmep) so
        // we can find the record offset by string search.
        // Result of the Import-from-Fab choice prompt. WPF's stock MessageBox
        // doesn't accept custom button labels, so this is a small custom
        // Window with explicit "Replace All" / "Merge" / "Cancel" buttons.
        private enum ImportChoice { Replace, Merge, Cancel }

        private ImportChoice ShowImportChoiceDialog(int total, int pipeCount, int ductCount, string fileName)
        {
            var dlg = new System.Windows.Window
            {
                Title                 = "Import from Fabrication Config",
                Width                 = 480,
                Height                = 260,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner                 = this,
                ResizeMode            = ResizeMode.NoResize,
            };
            var stack = new StackPanel { Margin = new Thickness(14) };
            stack.Children.Add(new TextBlock
            {
                Text         = $"Found {total} Hanger Spec(s) in {fileName}",
                FontWeight   = FontWeights.SemiBold,
                Margin       = new Thickness(0, 0, 0, 6),
            });
            stack.Children.Add(new TextBlock
            {
                Text   = $"  • {pipeCount} Pipe spec(s)\n  • {ductCount} Duct spec(s)",
                Margin = new Thickness(0, 0, 0, 12),
            });
            stack.Children.Add(new TextBlock
            {
                Text         = "Replace All — clear current Pipe + Duct spec lists and install the imported ones.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 4),
            });
            stack.Children.Add(new TextBlock
            {
                Text         = "Merge — add the imported specs alongside your existing ones.",
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 14),
            });

            var buttons = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var replace = new Button { Content = "Replace All", Padding = new Thickness(14, 5, 14, 5), IsDefault = true };
            var merge   = new Button { Content = "Merge",       Padding = new Thickness(14, 5, 14, 5), Margin  = new Thickness(8, 0, 0, 0) };
            var cancel  = new Button { Content = "Cancel",      Padding = new Thickness(14, 5, 14, 5), Margin  = new Thickness(8, 0, 0, 0), IsCancel = true };
            buttons.Children.Add(replace);
            buttons.Children.Add(merge);
            buttons.Children.Add(cancel);
            stack.Children.Add(buttons);
            dlg.Content = stack;

            ImportChoice result = ImportChoice.Cancel;
            replace.Click += (s, ev) => { result = ImportChoice.Replace; dlg.DialogResult = true; };
            merge.Click   += (s, ev) => { result = ImportChoice.Merge;   dlg.DialogResult = true; };
            dlg.ShowDialog();
            return result;
        }

        // ── Import from Fabrication Config ────────────────────────────────
        // Reads HSpecs.MAP from the active Fab config's Database folder and
        // brings the Hanger Specs into the dialog as SupportSpecs. The user
        // picks Merge (keep existing, add new) or Replace (clear current
        // domain's list, install imported). The imported specs mark the
        // model dirty so the user reviews them and explicitly hits Save Specs.
        private void ImportFromFab_Click(object sender, RoutedEventArgs e)
        {
            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                var doc = uiApp.ActiveUIDocument.Document;
                string? dbFolder = SupportMapDumper.TryGetDatabaseFolder(doc);
                string? path = null;
                if (!string.IsNullOrEmpty(dbFolder))
                {
                    string candidate = System.IO.Path.Combine(dbFolder!, "HSpecs.MAP");
                    if (System.IO.File.Exists(candidate)) path = candidate;
                }
                if (string.IsNullOrEmpty(path))
                {
                    // Fall back to a file picker — folder not yet configured.
                    // After the user picks a file we remember its parent on
                    // ProjectInformation so subsequent imports auto-locate.
                    Dispatcher.Invoke(() =>
                    {
                        var ofd = new Microsoft.Win32.OpenFileDialog
                        {
                            Title  = "Locate HSpecs.MAP",
                            Filter = "HSpecs.MAP|HSpecs.MAP|Fabrication map files (*.map)|*.map",
                            CheckFileExists = true,
                        };
                        if (!string.IsNullOrEmpty(dbFolder)) ofd.InitialDirectory = dbFolder;
                        if (ofd.ShowDialog(this) == true) path = ofd.FileName;
                    });
                    if (string.IsNullOrEmpty(path)) return;

                    // Remember the folder so the next import lands here.
                    var pickedFolder = System.IO.Path.GetDirectoryName(path!);
                    if (!string.IsNullOrEmpty(pickedFolder))
                    {
                        using var t = new Transaction(doc, "Remember Fab Database folder");
                        t.Start();
                        HangerSettingsStore.SetFabricationDatabaseFolder(doc, pickedFolder!);
                        t.Commit();
                    }
                }

                List<SupportSpec> imported;
                string? error = null;
                try
                {
                    var hspecs  = HSpecsMapReader.Read(path!);
                    imported    = HSpecsMapReader.ToSupportSpecs(hspecs);
                }
                catch (Exception ex)
                {
                    imported = new List<SupportSpec>();
                    error    = ex.Message;
                }

                Dispatcher.Invoke(() =>
                {
                    if (error != null)
                    {
                        ViewModel.StatusText = $"Import failed: {error}";
                        return;
                    }
                    if (imported.Count == 0)
                    {
                        ViewModel.StatusText =
                            $"Import found 0 importable specs in {System.IO.Path.GetFileName(path!)} " +
                            "(only PIPEWORK and DUCTWORK domains are supported).";
                        return;
                    }

                    int pipeCount = imported.Count(s => s.Domain == HangerDomain.Pipe);
                    int ductCount = imported.Count(s => s.Domain == HangerDomain.Duct);

                    var choice = ShowImportChoiceDialog(imported.Count, pipeCount, ductCount,
                                                       System.IO.Path.GetFileName(path!));
                    if (choice == ImportChoice.Cancel) return;
                    bool replace = choice == ImportChoice.Replace;

                    ViewModel.ImportSpecs(imported, replace);
                    ViewModel.StatusText =
                        $"Imported {imported.Count} spec(s) from " +
                        $"{System.IO.Path.GetFileName(path!)} " +
                        $"({(replace ? "REPLACE ALL" : "MERGE")}). " +
                        "Review the changes and click Save Specs to persist.";
                });
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        // ── Apply ────────────────────────────────────────────────────────────

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            var pipeSpec      = ViewModel.ApplyPipeSpec?.Snapshot();
            var roundDuctSpec = ViewModel.ApplyRoundDuctSpec?.Snapshot();
            var rectDuctSpec  = ViewModel.ApplyRectDuctSpec?.Snapshot();

            if (pipeSpec == null && roundDuctSpec == null && rectDuctSpec == null)
            {
                ViewModel.StatusText = "Pick a Pipe spec and/or Duct spec to apply.";
                return;
            }

            HangerSelectionMode mode = ViewModel.GetMode();
            HangerServiceScope  scope = ViewModel.IsScopeView ? HangerServiceScope.ActiveView : HangerServiceScope.WholeProject;
            string? selectedService   = ViewModel.SelectedServiceName;
            var prePickedIds          = ViewModel.PickedElementIds.ToList();
            var snapshotSpecsForSave  = ViewModel.SnapshotSpecs();
            ElementId? startId        = ViewModel.StartElementId;
            XYZ?       startConnOrigin = ViewModel.StartConnectorOrigin;

            // Gate: any spec using a flow-direction-dependent mode (Before / After)
            // requires a Start Node to be picked. Symmetric modes (NotAt /
            // BeforeAndAfter) don't care — apply without a start is fine.
            bool needsFlow = NeedsFlow(pipeSpec) || NeedsFlow(roundDuctSpec) || NeedsFlow(rectDuctSpec);
            if (needsFlow && startId == null)
            {
                System.Windows.MessageBox.Show(
                    this,
                    "This spec uses 'Before' or 'After' change-of-direction placement, " +
                    "which needs flow direction.\n\n" +
                    "Click 'Pick…' next to 'Starting Node' to define the start of the run, " +
                    "then click Apply again.",
                    "Starting Node required",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                ViewModel.StatusText =
                    "Apply blocked: pick a Starting Node first (required for Before/After modes).";
                return;
            }
            bool       reverseFlow    = ViewModel.ReverseFlow;  // always false now (UI removed)
            bool       attachToStruct = ViewModel.AttachToStructure;
            double     minSpacingFt   = ViewModel.MinSpacingEnabled
                                       ? Math.Max(0.0, ViewModel.MinSpacingInches / 12.0)
                                       : 0.0;
            bool       useMechEqStart = ViewModel.UseMechEqAsStart;

            HangerLayoutApp.HangerHandler!.SetAction(uiApp =>
            {
                var doc   = uiApp.ActiveUIDocument.Document;
                var uiDoc = uiApp.ActiveUIDocument;

                List<FabricationPart> parts;
                try
                {
                    parts = CollectParts(doc, uiDoc, mode, scope, selectedService, prePickedIds);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() => ViewModel.StatusText = $"Collection failed: {ex.Message}");
                    return;
                }

                if (parts.Count == 0)
                {
                    Dispatcher.Invoke(() => ViewModel.StatusText = "No fabrication pipes or ducts to process.");
                    return;
                }

                var (pipes, ducts) = PartitionByDomain(parts);

                // Filter to straight parts on each side
                pipes = pipes.Where(IsStraightPipework).ToList();
                ducts = ducts.Where(IsStraightDuctwork).ToList();

                var outcome = new HangerPlacer.Outcome();
                string? error = null;
                try
                {
                    using var tx = new Transaction(doc, "Hanger Layout — Apply");
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(new HangerWarningSwallower());
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);
                    tx.Start();

                    // Persist placement settings so they survive across sessions.
                    HangerSettingsStore.SetPlacementSettings(doc, new HangerSettingsStore.PlacementSettings
                    {
                        MinSpacingEnabled = ViewModel.MinSpacingEnabled,
                        MinSpacingInches  = ViewModel.MinSpacingInches,
                        UseMechEqAsStart  = ViewModel.UseMechEqAsStart,
                    });

                    // Delete existing hangers hosted on the target pipes/ducts
                    // before placing new ones. Prevents accumulation across
                    // repeated Apply runs (which would otherwise stack hangers
                    // visually and mask the actual current placement).
                    var targetIds = new HashSet<long>(
                        pipes.Concat(ducts).Select(p => p.Id.Value));
                    var hangersToDelete = new List<ElementId>();
                    foreach (var h in new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_FabricationHangers)
                        .WhereElementIsNotElementType()
                        .Cast<FabricationPart>())
                    {
                        try
                        {
                            var info = h.GetHostedInfo();
                            if (info != null && info.IsValidObject &&
                                targetIds.Contains(info.HostId.Value))
                            {
                                hangersToDelete.Add(h.Id);
                            }
                        }
                        catch { }
                    }
                    if (hangersToDelete.Count > 0)
                    {
                        try { doc.Delete(hangersToDelete); }
                        catch (Exception ex)
                        {
                            outcome.Notes.Add($"[clear-fail] {ex.Message}");
                        }
                        outcome.Notes.Add(
                            $"[clear] removed {hangersToDelete.Count} existing hanger(s) from target pipes/ducts");
                    }

                    // Persist current spec set alongside the apply, so the user doesn't
                    // lose edits if they forgot to hit Save Specs first.
                    HangerSpecStore.Save(doc, snapshotSpecsForSave);

                    // Resolve the effective start origin. If "Reverse flow" is
                    // checked, swap to the OTHER connector on the start part
                    // (matched by origin). We never let an INDEX cross between
                    // separate connector-enumeration calls — only origins.
                    XYZ? effectiveStartOrigin = startConnOrigin;
                    if (startId != null && startConnOrigin != null && reverseFlow)
                    {
                        var startPart = doc.GetElement(startId) as FabricationPart;
                        if (startPart != null)
                        {
                            var sConns = ConnectorHelper.GetPhysicalConnectors(startPart);
                            // Find the "other" connector by picking the one
                            // FURTHEST from the saved origin.
                            double bestD = -1;
                            foreach (var c in sConns)
                            {
                                double d = c.Origin.DistanceTo(startConnOrigin);
                                if (d > bestD) { bestD = d; effectiveStartOrigin = c.Origin; }
                            }
                        }
                    }

                    HangerFlowMap? flowMap = (startId != null && effectiveStartOrigin != null)
                        ? HangerFlowMap.Build(doc, startId, effectiveStartOrigin)
                        : null;
                    if (startId == null)
                    {
                        outcome.Notes.Add(
                            "[warn] No Start Node picked — Before/After modes fall back to " +
                            "SYMMETRIC anchoring (every fitting end gets a hanger). " +
                            "Click 'Pick Start' to define flow direction.");
                    }
                    if (flowMap != null)
                    {
                        string revNote = reverseFlow ? "  [reverse-flow]" : "";
                        var o = effectiveStartOrigin!;
                        outcome.Notes.Add(
                            $"[diag] flow map covers {flowMap.Count} part(s) " +
                            $"from start id {startId!.Value} " +
                            $"origin=({o.X:F3},{o.Y:F3},{o.Z:F3}){revNote}");
                        DumpFlowMapToFile(doc, flowMap, pipes.Concat(ducts).ToList(),
                                           startId!, effectiveStartOrigin!);
                        DumpStartPipeConnectivity(doc, startId!);
                    }

                    // Split ducts by detected shape so the user's Round vs
                    // Rectangular spec choices apply to the correct subset.
                    // Ambiguous shapes (DuctShape.Any) try the matching spec
                    // by preference order (Round first, then Rect), so they
                    // still get processed if either spec is set.
                    var roundDucts = ducts.Where(d => DetectDuctShape(d) == DuctShape.Round).ToList();
                    var rectDucts  = ducts.Where(d => DetectDuctShape(d) == DuctShape.Rectangular).ToList();
                    var anyDucts   = ducts.Where(d => DetectDuctShape(d) == DuctShape.Any).ToList();
                    // Send Any-shape ducts to whichever duct spec is configured
                    // first — keeps unrecognised content from being silently
                    // dropped.
                    if (anyDucts.Count > 0)
                    {
                        if (roundDuctSpec != null) roundDucts.AddRange(anyDucts);
                        else                       rectDucts.AddRange(anyDucts);
                    }

                    // Surface which spec(s) are being applied with which modes — the
                    // editor and the Apply dropdown can be pointing at different specs,
                    // so we name what actually drives placement.
                    if (pipeSpec != null)
                        outcome.Notes.Add(
                            $"[diag] applying Pipe spec '{pipeSpec.Name}': " +
                            $"SupportPositions={pipeSpec.SupportPositions}, " +
                            $"StraightJoints={pipeSpec.StraightJoints}");
                    if (roundDuctSpec != null && roundDucts.Count > 0)
                        outcome.Notes.Add(
                            $"[diag] applying Round Duct spec '{roundDuctSpec.Name}' " +
                            $"to {roundDucts.Count} duct(s): " +
                            $"SupportPositions={roundDuctSpec.SupportPositions}, " +
                            $"StraightJoints={roundDuctSpec.StraightJoints}");
                    if (rectDuctSpec != null && rectDucts.Count > 0)
                        outcome.Notes.Add(
                            $"[diag] applying Rect Duct spec '{rectDuctSpec.Name}' " +
                            $"to {rectDucts.Count} duct(s): " +
                            $"SupportPositions={rectDuctSpec.SupportPositions}, " +
                            $"StraightJoints={rectDuctSpec.StraightJoints}");

                    if (pipeSpec != null && pipes.Count > 0)
                        HangerPlacer.Place(doc, pipes, pipeSpec, outcome, flowMap, attachToStruct, minSpacingFt, useMechEqStart);
                    if (roundDuctSpec != null && roundDucts.Count > 0)
                        HangerPlacer.Place(doc, roundDucts, roundDuctSpec, outcome, flowMap, attachToStruct, minSpacingFt, useMechEqStart);
                    if (rectDuctSpec != null && rectDucts.Count > 0)
                        HangerPlacer.Place(doc, rectDucts, rectDuctSpec, outcome, flowMap, attachToStruct, minSpacingFt, useMechEqStart);

                    tx.Commit();
                }
                catch (Exception ex) { error = ex.Message; }

                string summary = error == null
                    ? FormatSummary(outcome, pipes.Count, ducts.Count)
                    : $"Apply failed: {error}";
                Dispatcher.Invoke(() => ViewModel.StatusText = summary);
            });
            HangerLayoutApp.HangerEvent!.Raise();
        }

        private static List<FabricationPart> CollectParts(
            Document doc, UIDocument uiDoc,
            HangerSelectionMode mode, HangerServiceScope scope, string? service,
            List<ElementId> prePicked)
        {
            switch (mode)
            {
                case HangerSelectionMode.CurrentSelection:
                    return uiDoc.Selection.GetElementIds()
                        .Select(id => doc.GetElement(id) as FabricationPart)
                        .Where(p => p != null).Cast<FabricationPart>()
                        .Where(IsPipeOrDuctCategory).ToList();

                case HangerSelectionMode.PickElements:
                    return prePicked
                        .Select(id => doc.GetElement(id) as FabricationPart)
                        .Where(p => p != null).Cast<FabricationPart>()
                        .Where(IsPipeOrDuctCategory).ToList();

                case HangerSelectionMode.AllService:
                {
                    if (string.IsNullOrWhiteSpace(service))
                        return new List<FabricationPart>();
                    var collector = scope == HangerServiceScope.ActiveView
                        ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                        : new FilteredElementCollector(doc);
                    return collector.OfClass(typeof(FabricationPart))
                        .WhereElementIsNotElementType()
                        .Cast<FabricationPart>()
                        .Where(p => string.Equals(p.ServiceName, service, StringComparison.OrdinalIgnoreCase))
                        .Where(IsPipeOrDuctCategory).ToList();
                }
            }
            return new List<FabricationPart>();
        }

        // Returns true if the spec uses a mode that needs flow direction
        // (Before / After). NotAt + BeforeAndAfter are symmetric.
        private static bool NeedsFlow(SupportSpec? spec)
        {
            if (spec == null) return false;
            bool posFlow =
                spec.SupportPositions == SupportPositionMode.BeforeChange ||
                spec.SupportPositions == SupportPositionMode.AfterChange;
            bool jointFlow =
                spec.StraightJoints == StraightJointMode.BeforeJoint ||
                spec.StraightJoints == StraightJointMode.AfterJoint;
            return posFlow || jointFlow;
        }

        private static bool IsPipeOrDuctCategory(FabricationPart fp)
        {
            if (fp.Category == null) return false;
            long cv = fp.Category.Id.Value;
            return cv == (long)BuiltInCategory.OST_FabricationPipework
                || cv == (long)BuiltInCategory.OST_FabricationDuctwork;
        }

        private static (List<FabricationPart> pipes, List<FabricationPart> ducts) PartitionByDomain(
            IEnumerable<FabricationPart> parts)
        {
            var pipes = new List<FabricationPart>();
            var ducts = new List<FabricationPart>();
            foreach (var fp in parts)
            {
                long cv = fp.Category?.Id.Value ?? 0;
                if      (cv == (long)BuiltInCategory.OST_FabricationPipework) pipes.Add(fp);
                else if (cv == (long)BuiltInCategory.OST_FabricationDuctwork) ducts.Add(fp);
            }
            return (pipes, ducts);
        }

        private static bool IsStraightPipework(FabricationPart fp)
        {
            try
            {
                if (fp.IsAHanger()) return false;
                // Welds have 2 anti-parallel equal-radius connectors so they pass
                // IsStraightPipe geometrically — but they aren't pipes and shouldn't
                // get hangers. Exclude them via the PCF type classifier.
                if (PartTypeClassifier.GetPcfType(fp) == "WELD") return false;
                return PartTypeClassifier.IsStraightPipe(fp);
            }
            catch { return false; }
        }

        private static bool IsStraightDuctwork(FabricationPart fp)
        {
            try
            {
                if (fp.IsAHanger()) return false;

                // Fast path: CID-based identification. The catalog CIDs for
                // straight duct sections are stable and deterministic — no
                // need to fall through to geometric tests if we know it's
                // a straight via CID.
                if (PartTypeClassifier.IsStraightDuctByCid(fp)) return true;

                // Fallback geometry test: 2 connectors, anti-parallel, equal
                // cross-section. Used when the CID set hasn't been populated
                // for a given content's straights.
                var conns = ConnectorHelper.GetPhysicalConnectors(fp);
                if (conns.Count != 2) return false;

                var c0 = conns[0]; var c1 = conns[1];
                var d0 = c0.CoordinateSystem.BasisZ;
                var d1 = c1.CoordinateSystem.BasisZ;
                if (Math.Abs(d0.DotProduct(d1)) < 0.999) return false;

                if (c0.Shape != c1.Shape) return false;
                if (c0.Shape == ConnectorProfileType.Round)
                    return Math.Abs(c0.Radius - c1.Radius) < 0.001;
                return Math.Abs(c0.Width  - c1.Width)  < 0.001
                    && Math.Abs(c0.Height - c1.Height) < 0.001;
            }
            catch { return false; }
        }

        private static void DumpStartPipeConnectivity(Document doc, ElementId startId)
        {
            try
            {
                if (doc.GetElement(startId) is not FabricationPart startPart) return;
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(docs)) docs = System.IO.Path.GetTempPath();
                string path = System.IO.Path.Combine(docs, "hanger_diag.txt");
                using var sw = new System.IO.StreamWriter(path, append: true);
                sw.WriteLine();
                sw.WriteLine($"=== Start pipe connector connectivity ===");
                sw.WriteLine($"Start part id: {startId.Value}");
                var conns = ConnectorHelper.GetPhysicalConnectors(startPart);
                for (int i = 0; i < conns.Count; i++)
                {
                    var c = conns[i];
                    bool conntd = false; try { conntd = c.IsConnected; } catch { }
                    int refsCount = 0; try { foreach (Connector _ in c.AllRefs) refsCount++; } catch { }
                    string origin = "?";
                    try { var o = c.Origin; origin = $"({o.X:F2},{o.Y:F2},{o.Z:F2})"; } catch { }
                    sw.WriteLine($"  conn[{i}] type={c.ConnectorType} IsConnected={conntd} " +
                                 $"AllRefs.count={refsCount} origin={origin}");
                }
            }
            catch { }
        }

        private static void DumpFlowMapToFile(
            Document doc, HangerFlowMap flowMap,
            List<FabricationPart> targets, ElementId startId, XYZ startOrigin)
        {
            try
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (string.IsNullOrWhiteSpace(docs)) docs = System.IO.Path.GetTempPath();
                string path = System.IO.Path.Combine(docs, "hanger_diag.txt");
                using var sw = new System.IO.StreamWriter(path, append: true);
                sw.WriteLine();
                sw.WriteLine($"=== Flow map dump @ {DateTime.Now:HH:mm:ss} ===");
                sw.WriteLine($"Start: id {startId.Value} origin=({startOrigin.X:F3},{startOrigin.Y:F3},{startOrigin.Z:F3})");
                sw.WriteLine($"Total mapped parts: {flowMap.Count}");
                sw.WriteLine();
                sw.WriteLine("Mapped parts (id : category : near-conn-origin):");
                foreach (var kv in flowMap.Entries)
                {
                    var elem = doc.GetElement(new ElementId(kv.Key));
                    string cat = "?";
                    string extra = "";
                    if (elem is FabricationPart fp)
                    {
                        cat = fp.Category?.Name ?? "?";
                        try
                        {
                            bool hanger = fp.IsAHanger();
                            if (hanger) extra = " [HANGER]";
                        }
                        catch { }
                        var conns = ConnectorHelper.GetPhysicalConnectors(fp);
                        if (conns.Count == 2)
                        {
                            double lenIn = conns[0].Origin.DistanceTo(conns[1].Origin) * 12.0;
                            extra += $" len={lenIn:F1}\"";
                        }
                    }
                    var p = kv.Value;
                    sw.WriteLine($"  {kv.Key} : {cat} : near=({p.X:F3},{p.Y:F3},{p.Z:F3}){extra}");
                }
                sw.WriteLine();
                sw.WriteLine("Target pipes/ducts (id : mapped?):");
                foreach (var fp in targets)
                {
                    bool mapped = flowMap.IsKnown(fp.Id);
                    sw.WriteLine($"  {fp.Id.Value} : {(mapped ? "MAPPED" : "UNMAPPED")}");
                }
            }
            catch { /* best-effort */ }
        }

        private static string FormatSummary(HangerPlacer.Outcome o, int nPipes, int nDucts)
        {
            string baseLine =
                $"Placed {o.Placed} hanger(s) on {nPipes} pipe(s) and {nDucts} duct(s). " +
                $"Skipped: {o.SkippedShort} too-short, {o.SkippedNoSpec} no-spec, " +
                $"{o.SkippedNoButton} no-hanger-button, {o.CreateFailed} create-failed" +
                (o.SkippedTooClose > 0 ? $", {o.SkippedTooClose} too-close" : "") + ". " +
                (o.OversizeBand > 0 ? $"{o.OversizeBand} part(s) exceeded all size bands (used largest). " : "");

            // Per-chain orientation tally — only show if any multi-segment
            // chains were processed (single-segment "chains" don't contribute).
            int chainTotal = o.ChainsOrientedByStartNode + o.ChainsOrientedByMechEq + o.ChainsOrientedAuto;
            if (chainTotal > 0)
            {
                var parts = new List<string>();
                if (o.ChainsOrientedByStartNode > 0) parts.Add($"{o.ChainsOrientedByStartNode} from Start Node");
                if (o.ChainsOrientedByMechEq    > 0) parts.Add($"{o.ChainsOrientedByMechEq} from Mech Eq");
                if (o.ChainsOrientedAuto        > 0) parts.Add($"{o.ChainsOrientedAuto} auto");
                baseLine += $"Chains oriented: {string.Join(", ", parts)}. ";
            }

            if (o.Notes.Count == 0) return baseLine;

            // Always surface diagnostic notes prefixed with [diag…], [anchor…],
            // [unmapped …], [skip …], [clear …] so the user can verify per-part
            // decisions and see which pipes fell back to symmetric anchoring
            // or got skipped (e.g. too short between two joint setbacks).
            var diagNotes = o.Notes
                .Where(n => n.StartsWith("[diag")     || n.StartsWith("[anchor")
                         || n.StartsWith("[unmapped") || n.StartsWith("[skip")
                         || n.StartsWith("[clear")    || n.StartsWith("[warn")
                         || n.StartsWith("[flow-bug"))
                .Distinct()
                .ToList();
            string diagLine = diagNotes.Count > 0
                ? "\n" + string.Join("\n", diagNotes)
                : "";

            if (o.Placed > 0) return baseLine + diagLine;

            var distinct = o.Notes.Where(n => !n.StartsWith("[diag"))
                                  .Select(NormaliseNote)
                                  .Distinct()
                                  .Take(4)
                                  .ToList();
            return baseLine + diagLine +
                   (distinct.Count > 0 ? "\nReasons: " + string.Join(" | ", distinct) : "");
        }

        // Strip the per-element prefix so we can collapse identical reasons across parts.
        private static string NormaliseNote(string note)
        {
            int closeBracket = note.IndexOf(']');
            return closeBracket >= 0 ? note.Substring(closeBracket + 1).Trim() : note;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ViewModel
    // ─────────────────────────────────────────────────────────────────────────

    public class HangerLayoutViewModel : INotifyPropertyChanged
    {
        public ObservableCollection<SpecVm> PipeSpecs { get; } = new();
        public ObservableCollection<SpecVm> DuctSpecs { get; } = new();

        public HangerLayoutViewModel(List<SupportSpec> initialSpecs, List<string> services)
        {
            foreach (var s in initialSpecs ?? new List<SupportSpec>())
            {
                var vm = SpecVm.From(s);
                if (s.Domain == HangerDomain.Duct) DuctSpecs.Add(vm);
                else                                PipeSpecs.Add(vm);
            }

            _services = (services ?? new List<string>()).ToList();
            ServiceNames = new ObservableCollection<string>(_services);
            if (_services.Count > 0) _selectedServiceName = _services[0];

            if (PipeSpecs.Count > 0) SelectedPipeSpec = PipeSpecs[0];
            if (DuctSpecs.Count > 0) SelectedDuctSpec = DuctSpecs[0];
            if (PipeSpecs.Count > 0) ApplyPipeSpec    = PipeSpecs[0];

            // Build the shape-filtered duct lists from initial DuctSpecs and
            // initialise the round/rect apply selections. After this point,
            // any add/remove on DuctSpecs OR any change to a SpecVm's
            // DuctShapeIndex re-runs the filter.
            RefreshShapeFilteredDuctSpecs();

            // Hook dirty tracking AFTER initial population — initial load
            // shouldn't mark the model dirty.
            HookSpecCollection(PipeSpecs);
            HookSpecCollection(DuctSpecs);
            foreach (var s in PipeSpecs) HookSpec(s);
            foreach (var s in DuctSpecs) HookSpec(s);

            // Re-filter the round/rect lists when:
            //   - the DuctSpecs collection changes (add / remove / clear)
            //   - any duct SpecVm's DuctShapeIndex changes
            DuctSpecs.CollectionChanged += (s, e) => RefreshShapeFilteredDuctSpecs();
            PropertyChangedEventHandler shapeHandler = (s, e) =>
            {
                if (e.PropertyName == nameof(SpecVm.DuctShapeIndex))
                    RefreshShapeFilteredDuctSpecs();
            };
            foreach (var s in DuctSpecs) s.PropertyChanged += shapeHandler;
            DuctSpecs.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null)
                    foreach (SpecVm sp in e.NewItems) sp.PropertyChanged += shapeHandler;
                if (e.OldItems != null)
                    foreach (SpecVm sp in e.OldItems) sp.PropertyChanged -= shapeHandler;
            };

            IsSpecsDirty = false;
        }

        // ── Dirty tracking ───────────────────────────────────────────────────
        // True when the user has made spec edits since the last explicit
        // "Save Specs" click. Drives the Save button color and the
        // unsaved-changes prompt on close. Note: Apply's implicit save does
        // NOT clear this — the user must hit Save Specs to acknowledge.
        private bool _isSpecsDirty;
        public bool IsSpecsDirty
        {
            get => _isSpecsDirty;
            set => SetField(ref _isSpecsDirty, value);
        }
        public void MarkSpecsClean() => IsSpecsDirty = false;
        private void MarkDirty() => IsSpecsDirty = true;

        private void HookSpecCollection(ObservableCollection<SpecVm> coll)
        {
            coll.CollectionChanged += (s, e) =>
            {
                MarkDirty();
                if (e.NewItems != null)
                    foreach (SpecVm sp in e.NewItems) HookSpec(sp);
                if (e.OldItems != null)
                    foreach (SpecVm sp in e.OldItems) UnhookSpec(sp);
            };
        }

        private void HookSpec(SpecVm spec)
        {
            spec.PropertyChanged += OnSpecPropertyChanged;
            spec.Rows.CollectionChanged += OnRowsCollectionChanged;
            foreach (var r in spec.Rows) HookRow(r);
        }

        private void UnhookSpec(SpecVm spec)
        {
            spec.PropertyChanged -= OnSpecPropertyChanged;
            spec.Rows.CollectionChanged -= OnRowsCollectionChanged;
            foreach (var r in spec.Rows) UnhookRow(r);
        }

        private void HookRow(RowVm row)   => row.PropertyChanged += OnRowPropertyChanged;
        private void UnhookRow(RowVm row) => row.PropertyChanged -= OnRowPropertyChanged;

        private void OnSpecPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Ignore derived/display properties that just bubble from changes
            // we already counted.
            if (e.PropertyName == nameof(SpecVm.DisplayName)
                || e.PropertyName == nameof(SpecVm.HangerOverrideDisplay))
                return;
            MarkDirty();
        }

        private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e) => MarkDirty();

        private void OnRowsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            MarkDirty();
            if (e.NewItems != null)
                foreach (RowVm r in e.NewItems) HookRow(r);
            if (e.OldItems != null)
                foreach (RowVm r in e.OldItems) UnhookRow(r);
        }

        // ── Selection ────────────────────────────────────────────────────────
        private SpecVm? _selectedPipeSpec;
        public SpecVm? SelectedPipeSpec
        {
            get => _selectedPipeSpec;
            set
            {
                if (SetField(ref _selectedPipeSpec, value))
                    OnPropertyChanged(nameof(HasSelectedPipeSpec));
            }
        }
        public bool HasSelectedPipeSpec => _selectedPipeSpec != null;

        private SpecVm? _selectedDuctSpec;
        public SpecVm? SelectedDuctSpec
        {
            get => _selectedDuctSpec;
            set
            {
                if (SetField(ref _selectedDuctSpec, value))
                    OnPropertyChanged(nameof(HasSelectedDuctSpec));
            }
        }
        public bool HasSelectedDuctSpec => _selectedDuctSpec != null;

        // ── Apply targets ────────────────────────────────────────────────────
        private SpecVm? _applyPipeSpec;
        public SpecVm? ApplyPipeSpec { get => _applyPipeSpec; set => SetField(ref _applyPipeSpec, value); }

        // Two duct slots, one per shape. Each dropdown filters DuctSpecs by
        // DuctShape: the Round combo shows specs tagged Round OR Any; the Rect
        // combo shows specs tagged Rectangular OR Any. Round and Rectangular
        // ducts in the target set are placed against their corresponding spec.
        private SpecVm? _applyRoundDuctSpec;
        public SpecVm? ApplyRoundDuctSpec
        {
            get => _applyRoundDuctSpec;
            set => SetField(ref _applyRoundDuctSpec, value);
        }
        private SpecVm? _applyRectDuctSpec;
        public SpecVm? ApplyRectDuctSpec
        {
            get => _applyRectDuctSpec;
            set => SetField(ref _applyRectDuctSpec, value);
        }

        public ObservableCollection<SpecVm> RoundDuctSpecs { get; } = new();
        public ObservableCollection<SpecVm> RectDuctSpecs  { get; } = new();

        /// <summary>Rebuild the Round / Rect filtered lists from `DuctSpecs`
        /// based on each spec's `DuctShapeIndex`. Specs tagged Any appear in
        /// BOTH filtered lists. Call after DuctSpecs collection changes or any
        /// spec's DuctShapeIndex changes.</summary>
        public void RefreshShapeFilteredDuctSpecs()
        {
            var prevRound = _applyRoundDuctSpec;
            var prevRect  = _applyRectDuctSpec;

            RoundDuctSpecs.Clear();
            RectDuctSpecs.Clear();
            foreach (var s in DuctSpecs)
            {
                var shape = (DuctShape)s.DuctShapeIndex;
                if (shape == DuctShape.Round       || shape == DuctShape.Any) RoundDuctSpecs.Add(s);
                if (shape == DuctShape.Rectangular || shape == DuctShape.Any) RectDuctSpecs.Add(s);
            }

            // Preserve the user's prior selection if it's still in the
            // filtered list; otherwise default to the first available.
            ApplyRoundDuctSpec = RoundDuctSpecs.Contains(prevRound!)
                ? prevRound
                : (RoundDuctSpecs.Count > 0 ? RoundDuctSpecs[0] : null);
            ApplyRectDuctSpec = RectDuctSpecs.Contains(prevRect!)
                ? prevRect
                : (RectDuctSpecs.Count > 0 ? RectDuctSpecs[0] : null);
        }

        // ── Selection mode ───────────────────────────────────────────────────
        private bool _isModeCurrent = true;
        public bool IsModeCurrent { get => _isModeCurrent; set => SetField(ref _isModeCurrent, value); }

        private bool _isModePick;
        public bool IsModePick { get => _isModePick; set => SetField(ref _isModePick, value); }

        private bool _isModeService;
        public bool IsModeService { get => _isModeService; set => SetField(ref _isModeService, value); }

        public HangerSelectionMode GetMode() =>
            _isModePick    ? HangerSelectionMode.PickElements
          : _isModeService ? HangerSelectionMode.AllService
          :                  HangerSelectionMode.CurrentSelection;

        // ── Service scope ────────────────────────────────────────────────────
        private bool _isScopeProject = true;
        public bool IsScopeProject { get => _isScopeProject; set => SetField(ref _isScopeProject, value); }

        private bool _isScopeView;
        public bool IsScopeView { get => _isScopeView; set => SetField(ref _isScopeView, value); }

        // ── Services dropdown ────────────────────────────────────────────────
        private List<string> _services;
        public ObservableCollection<string> ServiceNames { get; private set; }

        private string? _selectedServiceName;
        public string? SelectedServiceName { get => _selectedServiceName; set => SetField(ref _selectedServiceName, value); }

        public void SetServiceNames(List<string> names)
        {
            _services = names ?? new List<string>();
            ServiceNames.Clear();
            foreach (var n in _services) ServiceNames.Add(n);
            if (_selectedServiceName == null || !_services.Contains(_selectedServiceName))
                SelectedServiceName = _services.FirstOrDefault();
        }

        // ── Pre-picked elements (Pick mode) ──────────────────────────────────
        public List<ElementId> PickedElementIds { get; set; } = new();

        // ── Start node for flow-direction-aware Before/After modes ───────────
        // Store the connector's WORLD-POSITION ORIGIN, not its index, because
        // Revit's ConnectorManager.Connectors enumeration order is unstable —
        // it can flip between runs after the document is modified, so an index
        // saved at PickStart may refer to a different connector at Apply time.
        private ElementId? _startElementId;
        private XYZ?       _startConnectorOrigin;
        public ElementId? StartElementId        => _startElementId;
        public XYZ?       StartConnectorOrigin  => _startConnectorOrigin;
        private string _startElementLabel = string.Empty;
        public string StartElementText =>
            _startElementId == null ? string.Empty : _startElementLabel;

        public void SetStartElement(ElementId? id, XYZ? connectorOrigin, string label)
        {
            _startElementId       = id;
            _startConnectorOrigin = connectorOrigin;
            _startElementLabel    = label;
            OnPropertyChanged(nameof(StartElementText));
        }

        // Manual escape hatch: lets the user flip the BFS direction if the
        // picker landed on the wrong connector. Equivalent to picking the
        // OTHER connector on the start pipe. (UI hidden; kept for future use
        // — the new XAML doesn't expose it but the field stays so the value
        // is always a stable false.)
        private bool _reverseFlow;
        public bool ReverseFlow
        {
            get => _reverseFlow;
            set => SetField(ref _reverseFlow, value);
        }

        // ── Apply-time options ───────────────────────────────────────────────
        // When true, Revit's CreateHanger looks for overhead structural framing
        // and snaps the hanger rod to it. When false, the rod uses default
        // length and is not bound to any structural element. This is a per-run
        // toggle (not a spec property) because users typically flip it based
        // on the pass type — coordination drafts (off) vs fabrication-ready
        // (on) — across all hangers in a single Apply.
        private bool _attachToStructure;
        public bool AttachToStructure
        {
            get => _attachToStructure;
            set => SetField(ref _attachToStructure, value);
        }

        // Project-wide minimum-spacing skip. When enabled, walking start-to-end,
        // any candidate hanger position falling within MinSpacingInches of the
        // previously kept position is dropped. Earlier (start-side) wins.
        // Persisted via HangerSettingsStore.PlacementSettings.
        private bool _minSpacingEnabled;
        public bool MinSpacingEnabled
        {
            get => _minSpacingEnabled;
            set => SetField(ref _minSpacingEnabled, value);
        }
        private double _minSpacingInches = 6.0;
        public double MinSpacingInches
        {
            get => _minSpacingInches;
            set => SetField(ref _minSpacingInches, value);
        }

        // When true, the placer auto-orients each chain so the end closest
        // (via connector connectivity) to a Mechanical Equipment family
        // instance becomes the start side. Falls back to the existing auto
        // behavior when no equipment is reachable within the hop limit.
        // An explicit Start Node pick still wins on a per-Apply basis.
        private bool _useMechEqAsStart;
        public bool UseMechEqAsStart
        {
            get => _useMechEqAsStart;
            set => SetField(ref _useMechEqAsStart, value);
        }

        // ── Target-category presence (drives Pipe/Duct spec dropdown visibility) ──
        // True when the current target set (Current Selection / Picked / Service)
        // contains at least one Fabrication Pipework / Ductwork part respectively.
        // When false, the corresponding spec dropdown is replaced with a "—".
        private bool _hasPipeTarget = true;
        public bool HasPipeTarget
        {
            get => _hasPipeTarget;
            set => SetField(ref _hasPipeTarget, value);
        }
        // Split duct target detection by shape so the Round and Rect
        // dropdowns can show "—" independently.
        private bool _hasRoundDuctTarget = true;
        public bool HasRoundDuctTarget
        {
            get => _hasRoundDuctTarget;
            set => SetField(ref _hasRoundDuctTarget, value);
        }
        private bool _hasRectDuctTarget = true;
        public bool HasRectDuctTarget
        {
            get => _hasRectDuctTarget;
            set => SetField(ref _hasRectDuctTarget, value);
        }

        // ── Status ───────────────────────────────────────────────────────────
        private string _statusText = string.Empty;
        public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }

        // ── Spec list mutators ───────────────────────────────────────────────

        /// <summary>
        /// Bring in specs from an external source (e.g. Fab config HSpecs.MAP).
        /// When <paramref name="replace"/> is true, the existing Pipe and Duct
        /// lists are cleared first; otherwise the imported specs are appended.
        /// The collection-changed hooks already attached to PipeSpecs/DuctSpecs
        /// will mark `IsSpecsDirty=true`, so the Save Specs button highlights
        /// and the close prompt fires until the user saves.
        /// </summary>
        public void ImportSpecs(IEnumerable<SupportSpec> incoming, bool replace)
        {
            if (replace)
            {
                PipeSpecs.Clear();
                DuctSpecs.Clear();
            }
            foreach (var s in incoming)
            {
                var vm = SpecVm.From(s);
                if (s.Domain == HangerDomain.Duct) DuctSpecs.Add(vm);
                else                                PipeSpecs.Add(vm);
            }
            // Pick the first spec in each domain so the editor isn't blank.
            if (SelectedPipeSpec == null && PipeSpecs.Count > 0) SelectedPipeSpec = PipeSpecs[0];
            if (SelectedDuctSpec == null && DuctSpecs.Count > 0) SelectedDuctSpec = DuctSpecs[0];
            if (ApplyPipeSpec    == null && PipeSpecs.Count > 0) ApplyPipeSpec    = PipeSpecs[0];

            // CollectionChanged on DuctSpecs already triggers
            // RefreshShapeFilteredDuctSpecs, which sets ApplyRound/Rect.
        }

        public void AddSpec(HangerDomain domain)
        {
            var list = domain == HangerDomain.Pipe ? PipeSpecs : DuctSpecs;
            int n = list.Count + 1;
            string baseName = domain == HangerDomain.Pipe ? "Pipe Spec" : "Duct Spec";
            var spec = new SupportSpec { Name = $"{baseName} {n}", Domain = domain };
            spec.Rows.Add(new SupportSpecRow
            {
                MaxSizeInches = 12,
                StraightSpacingInches = 96,
                FittingDistanceInches = 18,
                DistanceFromJointInches = 9
            });
            var vm = SpecVm.From(spec);
            list.Add(vm);
            if (domain == HangerDomain.Pipe) SelectedPipeSpec = vm;
            else                              SelectedDuctSpec = vm;
        }

        public void DuplicateSelectedSpec(HangerDomain domain)
        {
            var src = domain == HangerDomain.Pipe ? SelectedPipeSpec : SelectedDuctSpec;
            if (src == null) return;
            var snap = src.Snapshot();
            snap.Id = Guid.NewGuid();
            snap.Name = src.Name + " (copy)";
            var vm = SpecVm.From(snap);
            var list = domain == HangerDomain.Pipe ? PipeSpecs : DuctSpecs;
            list.Add(vm);
            if (domain == HangerDomain.Pipe) SelectedPipeSpec = vm;
            else                              SelectedDuctSpec = vm;
        }

        public void DeleteSelectedSpec(HangerDomain domain)
        {
            var list = domain == HangerDomain.Pipe ? PipeSpecs : DuctSpecs;
            var sel  = domain == HangerDomain.Pipe ? SelectedPipeSpec : SelectedDuctSpec;
            if (sel == null) return;
            int idx = list.IndexOf(sel);
            list.Remove(sel);
            var newSel = list.Count == 0 ? null
                       : list[Math.Max(0, Math.Min(idx, list.Count - 1))];
            if (domain == HangerDomain.Pipe) SelectedPipeSpec = newSel;
            else                              SelectedDuctSpec = newSel;
        }

        public void AddRow(HangerDomain domain)
        {
            var spec = domain == HangerDomain.Pipe ? SelectedPipeSpec : SelectedDuctSpec;
            if (spec == null) return;
            var lastRow = spec.Rows.LastOrDefault();
            spec.Rows.Add(new RowVm
            {
                MaxSizeInches           = lastRow != null ? lastRow.MaxSizeInches * 2 : 12,
                StraightSpacingInches   = lastRow != null ? lastRow.StraightSpacingInches : 96,
                FittingDistanceInches   = lastRow != null ? lastRow.FittingDistanceInches : 18,
                DistanceFromJointInches = lastRow != null ? lastRow.DistanceFromJointInches : 9,
            });
        }

        public void DeleteSelectedRow(HangerDomain domain, RowVm? row)
        {
            var spec = domain == HangerDomain.Pipe ? SelectedPipeSpec : SelectedDuctSpec;
            if (spec == null) return;
            if (row == null && spec.Rows.Count > 0) row = spec.Rows[spec.Rows.Count - 1];
            if (row != null) spec.Rows.Remove(row);
        }

        // ── Snapshot for persistence ─────────────────────────────────────────

        public List<SupportSpec> SnapshotSpecs()
        {
            var all = new List<SupportSpec>();
            foreach (var s in PipeSpecs) all.Add(s.Snapshot());
            foreach (var s in DuctSpecs) all.Add(s.Snapshot());
            return all;
        }

        // ── INotifyPropertyChanged ───────────────────────────────────────────
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; OnPropertyChanged(n); return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SpecVm — bindable wrapper around SupportSpec
    // ─────────────────────────────────────────────────────────────────────────

    public class SpecVm : INotifyPropertyChanged
    {
        public Guid Id { get; set; }
        public HangerDomain Domain { get; set; }

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set { if (SetField(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); }
        }
        public string DisplayName => _name;

        private string? _hangerOverride;
        public string? HangerOverride
        {
            get => _hangerOverride;
            set { if (SetField(ref _hangerOverride, value)) OnPropertyChanged(nameof(HangerOverrideDisplay)); }
        }
        public string HangerOverrideDisplay =>
            string.IsNullOrWhiteSpace(_hangerOverride)
                ? "(first non-excluded hanger that fits the shape) or"
                : _hangerOverride!;

        private int _supportPositionsIndex = (int)SupportPositionMode.BeforeAndAfterChange;
        public int SupportPositionsIndex
        {
            get => _supportPositionsIndex;
            set => SetField(ref _supportPositionsIndex, value);
        }

        private int _straightJointsIndex = (int)StraightJointMode.NotAtJoint;
        public int StraightJointsIndex
        {
            get => _straightJointsIndex;
            set => SetField(ref _straightJointsIndex, value);
        }

        // Shape filter — only meaningful for Duct specs. Bound by the Duct
        // Specs editor's Shape combo (Any / Round / Rectangular).
        private int _ductShapeIndex = (int)DuctShape.Any;
        public int DuctShapeIndex
        {
            get => _ductShapeIndex;
            set => SetField(ref _ductShapeIndex, value);
        }

        public ObservableCollection<RowVm> Rows { get; } = new();

        public static SpecVm From(SupportSpec s)
        {
            var vm = new SpecVm
            {
                Id                   = s.Id == Guid.Empty ? Guid.NewGuid() : s.Id,
                Domain               = s.Domain,
                Name                 = s.Name,
                HangerOverride       = s.HangerOverride,
                SupportPositionsIndex = (int)s.SupportPositions,
                StraightJointsIndex   = (int)s.StraightJoints,
                DuctShapeIndex        = (int)s.DuctShape,
            };
            foreach (var r in s.Rows ?? new List<SupportSpecRow>())
            {
                vm.Rows.Add(new RowVm
                {
                    MaxSizeInches           = r.MaxSizeInches,
                    StraightSpacingInches   = r.StraightSpacingInches,
                    FittingDistanceInches   = r.FittingDistanceInches,
                    DistanceFromJointInches = r.DistanceFromJointInches,
                });
            }
            return vm;
        }

        public SupportSpec Snapshot()
        {
            return new SupportSpec
            {
                Id               = Id,
                Domain           = Domain,
                Name             = Name,
                HangerOverride   = HangerOverride,
                SupportPositions = (SupportPositionMode)_supportPositionsIndex,
                StraightJoints   = (StraightJointMode)_straightJointsIndex,
                DuctShape        = (DuctShape)_ductShapeIndex,
                Rows             = Rows.Select(r => new SupportSpecRow
                {
                    MaxSizeInches           = r.MaxSizeInches,
                    StraightSpacingInches   = r.StraightSpacingInches,
                    FittingDistanceInches   = r.FittingDistanceInches,
                    DistanceFromJointInches = r.DistanceFromJointInches,
                }).ToList()
            };
        }

        public override string ToString() => Name;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; OnPropertyChanged(n); return true;
        }
    }

    public class RowVm : INotifyPropertyChanged
    {
        private double _maxSizeInches;
        public double MaxSizeInches { get => _maxSizeInches; set => SetField(ref _maxSizeInches, value); }

        private double _straightSpacingInches;
        public double StraightSpacingInches { get => _straightSpacingInches; set => SetField(ref _straightSpacingInches, value); }

        private double _fittingDistanceInches;
        public double FittingDistanceInches { get => _fittingDistanceInches; set => SetField(ref _fittingDistanceInches, value); }

        private double _distanceFromJointInches;
        public double DistanceFromJointInches { get => _distanceFromJointInches; set => SetField(ref _distanceFromJointInches, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? n = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; OnPropertyChanged(n); return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Hanger button picker — small modal sub-dialog
    // ─────────────────────────────────────────────────────────────────────────

    public class HangerButtonEntry
    {
        public string ServiceName { get; set; } = string.Empty;
        public string GroupName   { get; set; } = string.Empty;
        public string ButtonName  { get; set; } = string.Empty;
        public string Display     => $"{ServiceName}  ›  {GroupName}  ›  {ButtonName}";
        public string OverrideKey => $"{GroupName}|{ButtonName}";
    }

    public class HangerButtonPickerDialog : Window
    {
        public HangerButtonEntry? SelectedEntry { get; private set; }

        public HangerButtonPickerDialog(List<HangerButtonEntry> entries)
        {
            Title = "Pick a Hanger Button";
            Width = 520;
            Height = 420;
            Topmost = true;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var grid = new System.Windows.Controls.Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var list = new ListBox
            {
                ItemsSource = entries,
                DisplayMemberPath = nameof(HangerButtonEntry.Display),
                Margin = new Thickness(8)
            };
            System.Windows.Controls.Grid.SetRow(list, 0);
            grid.Children.Add(list);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 80 };
            ok.Click += (_, _) =>
            {
                SelectedEntry = list.SelectedItem as HangerButtonEntry;
                DialogResult = SelectedEntry != null;
                Close();
            };
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            btnPanel.Children.Add(ok);
            btnPanel.Children.Add(cancel);
            System.Windows.Controls.Grid.SetRow(btnPanel, 1);
            grid.Children.Add(btnPanel);

            Content = grid;

            if (entries.Count == 0)
            {
                list.Items.Clear();
                grid.Children.Insert(0, new TextBlock
                {
                    Text = "No hangers found in any loaded service.",
                    Margin = new Thickness(16),
                    Foreground = System.Windows.Media.Brushes.Gray
                });
            }
        }
    }
}
