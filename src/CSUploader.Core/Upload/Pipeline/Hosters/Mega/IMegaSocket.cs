// <copyright file="IMegaSocket.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using System.Net.WebSockets;

namespace CSUploader.Upload.Pipeline.Hosters.Mega;

/// <summary>
/// The three operations <see cref="MegaWebSocketUploader"/> performs on a web socket, and nothing
/// else — deliberately narrower than <see cref="ClientWebSocket"/>.
/// <para>
/// It exists so a test can drive <c>UploadAsync</c> itself rather than the helpers underneath it.
/// Without it, every assertion about MEGA's pacing had to call
/// <c>SendChunkThrottledAsync</c> directly, which meant deleting the production call in
/// <c>UploadAsync</c> — going back to a single raw <c>SendAsync</c> per chunk — left the whole suite
/// green. MEGA and transfer.it ignoring the user's speed limit is exactly the bug that was shipped
/// once already; this is what makes a return to it fail a test.
/// </para>
/// </summary>
internal interface IMegaSocket : IDisposable
{
    Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

    Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken);

    Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>The real socket. A pass-through by design: anything it did beyond forwarding would be
/// behaviour the fake does not have, and so behaviour no test covers.</summary>
internal sealed class MegaClientWebSocket : IMegaSocket
{
    private readonly ClientWebSocket _socket = new();

    public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
        => _socket.ConnectAsync(uri, cancellationToken);

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        => _socket.ReceiveAsync(buffer, cancellationToken);

    public void Dispose() => _socket.Dispose();
}
