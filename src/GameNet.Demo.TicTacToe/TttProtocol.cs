using GameNet.Core;

namespace GameNet.Demo.TicTacToe;

// Wire contracts. Field order = declaration order; Write/Read/IWireCodec are generated.

[Packet]
public readonly partial record struct Ack(bool Ok, string Message);

[Packet]
public readonly partial record struct QueueResult(MatchStatus Status, string Message);

[Packet]
public readonly partial record struct MoveRequest(byte Cell);