using System.IO;
using System.IO.Pipes;
using System.Text;

namespace VideoFly;

/// <summary>
/// Un seul VideoFly à la fois : quand on double-clique sur une vidéo alors que VideoFly est déjà ouvert,
/// le nouveau lancement transmet le fichier à la fenêtre existante (qui l'ouvre dans une vignette) puis se ferme.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly string Id = "VideoFly-" + Environment.UserName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Vrai si ce lancement est le premier (c'est lui qui affiche la fenêtre).</summary>
    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Id, out bool createdNew);
        IsFirst = createdNew;
    }

    /// <summary>Envoie les fichiers à VideoFly déjà ouvert. Renvoie faux si celui-ci ne répond pas.</summary>
    public static bool SendToFirstInstance(IEnumerable<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(5000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var path in paths)
                writer.WriteLine(Path.GetFullPath(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Écoute les lancements suivants ; <paramref name="onFiles"/> est appelé hors du thread UI.</summary>
    public void StartListening(Action<IReadOnlyList<string>> onFiles)
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var files = new List<string>();
                    string? line;
                    while ((line = await reader.ReadLineAsync()) != null)
                        if (line.Length > 0) files.Add(line);
                    onFiles(files); // liste vide : simple demande de mise au premier plan
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // Connexion interrompue : on attend la suivante.
                }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
