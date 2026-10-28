using TabletMonitor;
using TabletMonitor.Core;

/// <summary>Testa a tradução toque → mouse/trackpad (usada no macOS) com um "mouse" falso.</summary>
static class GestureTest
{
    sealed class Sink : IPointerSink
    {
        public readonly List<string> Events = new();
        public void Mouse(MouseKind k, double x, double y, int c) { if (k != MouseKind.Move && k != MouseKind.LeftDrag) Events.Add($"{k}({x:0},{y:0}){(c > 1 ? "x" + c : "")}"); else if (k == MouseKind.LeftDrag) Events.Add("Drag"); }
        public void Scroll(int dy, int dx) => Events.Add($"Scroll({dy},{dx})");
    }

    static int fails;
    static void Check(string name, Action<GestureMapper> script, Func<List<string>, bool> ok)
    {
        var sink = new Sink();
        using var g = new GestureMapper(sink);
        script(g);
        Thread.Sleep(150);
        var compact = new List<string>();
        foreach (var e in sink.Events) if (!(e == "Drag" && compact.Count > 0 && compact[^1] == "Drag")) compact.Add(e);
        bool pass = ok(sink.Events);
        if (!pass) fails++;
        Console.WriteLine($"{(pass ? "OK  " : "FAIL")} {name}: {string.Join(" ", compact)}");
    }

    public static void Run()
    {
        Check("toque = clique", g => { g.Handle(TouchAction.Down, 0, 100, 100); Thread.Sleep(20); g.Handle(TouchAction.Up, 0, 100, 100); },
            e => e.SequenceEqual(new[] { "LeftDown(100,100)", "LeftUp(100,100)" }));
        Check("dois toques = clique duplo", g =>
        {
            g.Handle(TouchAction.Down, 0, 100, 100); Thread.Sleep(20); g.Handle(TouchAction.Up, 0, 100, 100); Thread.Sleep(120);
            g.Handle(TouchAction.Down, 0, 102, 101); Thread.Sleep(20); g.Handle(TouchAction.Up, 0, 102, 101);
        }, e => e.Count == 4 && e[2] == "LeftDown(100,100)x2" == false && e[2].EndsWith("x2"));
        Check("arrastar", g =>
        {
            g.Handle(TouchAction.Down, 0, 100, 100);
            for (int i = 1; i <= 5; i++) { Thread.Sleep(10); g.Handle(TouchAction.Move, 0, 100 + i * 20, 100); }
            g.Handle(TouchAction.Up, 0, 200, 100);
        }, e => e[0] == "LeftDown(100,100)" && e.Contains("Drag") && e[^1] == "LeftUp(200,100)");
        Check("segurar parado e soltar = seleção (botão segurado)", g => { g.Handle(TouchAction.Down, 0, 50, 50); Thread.Sleep(200); g.Handle(TouchAction.Up, 0, 50, 50); },
            e => e.SequenceEqual(new[] { "LeftDown(50,50)", "LeftUp(50,50)" }));
        Check("2 dedos tocando = botão direito (sem clique esquerdo)", g =>
        {
            g.Handle(TouchAction.Down, 0, 100, 100); Thread.Sleep(15); g.Handle(TouchAction.Down, 1, 140, 100);
            Thread.Sleep(60); g.Handle(TouchAction.Up, 0, 100, 100); g.Handle(TouchAction.Up, 1, 140, 100);
        }, e => e.SequenceEqual(new[] { "RightDown(120,100)", "RightUp(120,100)" }));
        Check("2 dedos arrastando = rolagem (sem cliques)", g =>
        {
            g.Handle(TouchAction.Down, 0, 100, 300); Thread.Sleep(10); g.Handle(TouchAction.Down, 1, 160, 300);
            for (int i = 1; i <= 6; i++) { Thread.Sleep(10); g.Handle(TouchAction.Move, 0, 100, 300 - i * 15); g.Handle(TouchAction.Move, 1, 160, 300 - i * 15); }
            g.Handle(TouchAction.Up, 0, 100, 210); g.Handle(TouchAction.Up, 1, 160, 210);
        }, e => e.All(x => x.StartsWith("Scroll")) && e.Count > 0 && e.Sum(x => int.Parse(x[7..x.IndexOf(',')])) == -90);
        Check("modo desligado ignora tudo", g => { g.Mode = InputMode.Off; g.Handle(TouchAction.Down, 0, 1, 1); g.Handle(TouchAction.Up, 0, 1, 1); },
            e => e.Count == 0);
        Console.WriteLine(fails == 0 ? "TODOS OS TESTES DE GESTOS PASSARAM" : $"{fails} FALHA(S)");
    }
}
