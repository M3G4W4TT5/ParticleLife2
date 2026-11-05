namespace ParticleLife.MonoGame.Editor;

/// <summary>Keeps first-use guidance inside the explicitly opened, keyboard-scrollable help panel.</summary>
internal static class QuickstartHelp
{
    internal static void AddTo(UiPanel panel, Func<string>? latestFeedback)
    {
        UiSection start = new("Start here", true);
        Add(start, "1. Space pauses or resumes. N advances one step. Discoveries > Starting recipe > New from recipe starts a fresh experiment.");
        Add(start, "2. Interactions edits receiver <- source. Positive outer coefficients attract; negative repel. Ctrl+Z undoes an edit at the current clock.");
        Add(start, "3. Wheel zooms around the pointer; middle/right drag pans. V fits the world. E hides the inspector for more canvas.");
        Add(start, "4. Particles > Brushes selects a tool. Drag on the canvas to apply it. Escape stops the stroke; Ctrl+Z undoes its applied edits.");
        Add(start, "5. Discoveries opens the file library. Save pattern stores a state to continue; Save session retains the applied history and actual GPU captures.");
        Add(start, "6. Playback offers CPU double and GPU float. GPU selection explicitly rounds particle values; switching back captures actual state. Future trajectories can differ.");
        panel.Content.Add(start);

        UiSection changes = new("Reset, Resample and New", true);
        Add(changes, "Reset restores the exact baseline particles, rules and zero clock. Live edits do not silently replace that baseline.");
        Add(changes, "Resample explicitly replaces positions/velocities using the future recipe. It keeps the current clock and can be undone.");
        Add(changes, "New from recipe creates that recipe's population, settings and zero-clock baseline. Apply matrix live changes only compatible interaction coefficients.");
        panel.Content.Add(changes);

        UiSection files = new("Choose the file for the job", false);
        Add(files, "Save preset stores reusable rules, generation, palette and display choices. Save pattern stores actual completed values at a new zero-time origin.");
        Add(files, "Initial exports the Reset baseline. Session retains actions at completed-step boundaries, including edits after the final numerical step.");
        Add(files, "A capture-backed session restores recorded values, or explicitly re-simulates its actions. CPU/GPU re-simulation can follow a different trajectory.");
        Add(files, "Keep .viewer.json companions beside scientific files. Existing output names are protected; reports and PNG/GIF images are not state inputs.");
        panel.Content.Add(files);

        UiSection access = new("Reading and keyboard access", false);
        Add(access, "Tab / Shift+Tab moves focus and scrolls the focused control into view. Enter or Space activates buttons; Escape cancels drafts and menus.");
        Add(access, "Page Up / Page Down scrolls this guide. In the inspector, focus a control or place the pointer over the panel before paging.");
        Add(access, "With canvas focus, Ctrl+plus/minus adjusts UI scale; Ctrl+0 restores 1x. Appearance also has exact UI scale. Small windows fit the controls automatically.");
        Add(access, "Try Economy display quality if trails or glow obscure a dense pattern. All display choices remain editable and never reduce the particle count.");
        panel.Content.Add(access);

        UiSection feedback = new("Latest feedback", false);
        feedback.Children.Add(new UiLabel(() => latestFeedback?.Invoke() ?? "Control validation errors appear in the viewer notification and launch console.", 124));
        panel.Content.Add(feedback);
    }

    private static void Add(UiSection section, string text) => section.Children.Add(new UiLabel(() => text, 64));
}
