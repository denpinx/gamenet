using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

/// <summary>
/// One match driven entirely by <see cref="TypedWorld{T}.Changed"/>: the event renders the board
/// and, when it is our turn, asks for input. There is no polling loop.
/// </summary>
public sealed class MatchSession : IDisposable
{
    private readonly TttClient _client;
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IDisposable _subscription;
    private byte _mySymbol;
    private string _opponent = string.Empty;
    private bool _started;
    private MatchBoard? _last;

    public MatchSession(TttClient client)
    {
        _client = client;
        _subscription = client.Matches.OnChanged(OnChanged);
    }

    public Task Completion => _completed.Task;

    /// <summary>Renders an already-existing match (e.g. resumed after login) without waiting for an Add event.</summary>
    public async Task RefreshAsync()
    {
        if (_client.Matches.Count == 0) return;
        foreach (var board in _client.Matches.Entities.Values)
        {
            await HandleAsync(board);
            return;
        }
    }

    private void OnChanged(TypedCollectionChanged<int, MatchBoard> update)
    {
        MatchBoard? latest = null;
        foreach (var change in update.Changes)
            if (change.Kind != ChangeKind.Removed && change.Current is { } board)
                latest = board;

        if (latest is { } value)
        {
            // Never call RPC from the receive pump: dispatch, then await the response.
            _ = Task.Run(() => HandleAsync(value));
        }
    }

    private async Task HandleAsync(MatchBoard board)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_started)
            {
                var view = await _client.StateAsync();
                _mySymbol = view.MySymbol;
                _opponent = view.Opponent;
                _started = true;
                Console.WriteLine($"对局 #{view.MatchId}：你={BoardText.Mark(_mySymbol)} 对手={_opponent}");
            }

            if (_last is { } previous && SameBoard(previous, board)) return;
            _last = board;

            PrintBoard(board, _mySymbol, _opponent);
            if (board.Status != MatchStatus.Playing)
            {
                Console.WriteLine(BoardText.Result(board.Status, board.Winner, _mySymbol));
                _completed.TrySetResult();
                return;
            }

            if (board.Turn == _mySymbol) await PromptMoveAsync();
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            Console.WriteLine($"[reconnect] {ex.Message}");
            _started = false;
            _last = null;
            if (!await _client.ReconnectAsync())
            {
                Console.WriteLine("[reconnect] 失败");
                _completed.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[match] {ex.Message}");
            _completed.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PromptMoveAsync()
    {
        Console.Write("轮到你，落子 (0-8)：");
        var input = Console.ReadLine();
        if (!int.TryParse(input, out var cell))
        {
            Console.WriteLine("无效输入（等下一次对局更新再试）");
            return;
        }

        var result = await _client.MoveAsync(cell);
        if (!result.Ok) Console.WriteLine(result.Message);
    }

    private static bool SameBoard(MatchBoard a, MatchBoard b) =>
        a.Turn == b.Turn && a.Status == b.Status && a.Winner == b.Winner &&
        a.Board.AsSpan().SequenceEqual(b.Board);

    private static void PrintBoard(MatchBoard board, byte mySymbol, string opponent)
    {
        Console.WriteLine();
        Console.WriteLine(BoardText.Render(board.Board));
        Console.WriteLine($"你={BoardText.Mark(mySymbol)} 对手={opponent} 轮到={BoardText.Mark(board.Turn)}");
    }

    private static bool IsTransient(Exception ex) =>
        ex is TimeoutException or RpcException or InvalidOperationException;

    public void Dispose()
    {
        _subscription.Dispose();
        _completed.TrySetResult();
    }
}
