﻿//
// Program.cs
//
// Author: Jeffrey Stedfast <jeff@xamarin.com>
//
// Copyright (c) 2014 Xamarin Inc. (www.xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Net;
using System.Linq;
using System.Threading;
using System.Collections.Generic;

using MailKit.Net.Imap;
using MailKit;

namespace ImapIdle {
	class Program
	{
		public static void Main (string[] args)
		{
			var logger = new ProtocolLogger (Console.OpenStandardError ());

			using (var client = new ImapClient (logger)) {
				var credentials = new NetworkCredential ("username@gmail.com", "password");
				var uri = new Uri ("imaps://imap.gmail.com");

				client.Connect (uri);

				// Remove the XOAUTH2 authentication mechanism since we don't have an OAuth2 token.
				client.AuthenticationMechanisms.Remove ("XOAUTH2");

				client.Authenticate (credentials);

				client.Inbox.Open (FolderAccess.ReadOnly);

				// keep track of the messages
				IList<IMessageSummary> messages = null;
				int count = 0;

				if (client.Inbox.Count > 0) {
					messages = client.Inbox.Fetch (0, -1, MessageSummaryItems.Full | MessageSummaryItems.UniqueId).ToList ();
					count = messages.Count;
				}

				// connect to some events...
				client.Inbox.CountChanged += (sender, e) => {
					// Note: the CountChanged event can fire for one of two reasons:
					//
					// 1. New messages have arrived in the folder.
					// 2. Messages have been expunged from the folder.
					//
					// If messages have been expunged, then the MessageExpunged event
					// should also fire and it should fire *before* the CountChanged
					// event fires.
					var folder = (ImapFolder) sender;

					if (folder.Count > count) {
						// New messages have arrived in the folder.
						Console.WriteLine ("{0}: {1} new messages have arrived.", folder, folder.Count - count);

						// Note: your first instict may be to fetch these new messages now, but you cannot do
						// that in an event handler (the ImapFolder is not re-entrant).
					} else if (folder.Count < count) {
						// Note: this shouldn't happen since we are decrementing count in the MessageExpunged handler.
						Console.WriteLine ("{0}: {1} messages have been removed.", folder, count - folder.Count);
					} else {
						// We just got a CountChanged event after 1 or more MessageExpunged events.
						Console.WriteLine ("{0}: the message count is now {1}.", folder, folder.Count);
					}

					// update our count so we can keep track of whether or not CountChanged events
					// signify new mail arriving.
					count = folder.Count;
				};

				client.Inbox.MessageExpunged += (sender, e) => {
					var folder = (ImapFolder) sender;

					if (e.Index < messages.Count) {
						var message = messages[e.Index];

						Console.WriteLine ("{0}: expunged message {1}: Subject: {2}", folder, e.Index, message.Envelope.Subject);

						// Note: If you are keeping a local cache of message information
						// (e.g. MessageSummary data) for the folder, then you'll need
						// to remove the message at e.Index.
						messages.RemoveAt (e.Index);
					} else {
						Console.WriteLine ("{0}: expunged message {1}: Unknown message.", folder, e.Index);
					}

					// update our count so we can keep track of whether or not CountChanged events
					// signify new mail arriving.
					count--;
				};

				client.Inbox.MessageFlagsChanged += (sender, e) => {
					var folder = (ImapFolder) sender;

					Console.WriteLine ("{0}: flags for message {1} have changed to: {2}.", folder, e.Index, e.Flags);
				};

				Console.WriteLine ("Hit any key to end the IDLE loop.");
				using (var done = new CancellationTokenSource ()) {
					var thread = new Thread (IdleLoop);

					thread.Start (new IdleState (client, done.Token));

					Console.ReadKey ();
					done.Cancel ();
					thread.Join ();
				}

				if (count > messages.Count) {
					Console.WriteLine ("The new messages that arrived during IDLE are:");
					foreach (var message in client.Inbox.Fetch (messages.Count, -1, MessageSummaryItems.Full | MessageSummaryItems.UniqueId))
						Console.WriteLine ("Subject: {0}", message.Envelope.Subject);
				}

				client.Disconnect (true);
			}
		}

		class IdleState
		{
			readonly object mutex = new object ();
			CancellationTokenSource timeout;

			public CancellationToken CancellationToken { get; private set; }
			public CancellationToken DoneToken { get; private set; }
			public ImapClient Client { get; private set; }

			public bool IsCancellationRequested {
				get {
					return CancellationToken.IsCancellationRequested || DoneToken.IsCancellationRequested;
				}
			}

			public IdleState (ImapClient client, CancellationToken doneToken, CancellationToken cancellationToken = default (CancellationToken))
			{
				CancellationToken = cancellationToken;
				DoneToken = doneToken;
				Client = client;

				// When the user hits a key, end the current timeout as well
				doneToken.Register (CancelTimeout);
			}

			void CancelTimeout ()
			{
				lock (mutex) {
					if (timeout != null)
						timeout.Cancel ();
				}
			}

			public void SetTimeoutSource (CancellationTokenSource source)
			{
				lock (mutex) {
					timeout = source;

					if (timeout != null && IsCancellationRequested)
						timeout.Cancel ();
				}
			}
		}

		static void IdleLoop (object state)
		{
			var idle = (IdleState) state;

			while (!idle.IsCancellationRequested)
			{
				using (var timeout = new CancellationTokenSource ()) {
					using (var timer = new System.Timers.Timer (9 * 60 * 1000)) {
						// End the IDLE command after 9 minutes... (most servers will disconnect the client after 10 minutes)
						timer.Elapsed += (sender, e) => timeout.Cancel ();
						timer.AutoReset = false;
						timer.Enabled = true;

						try {
							idle.SetTimeoutSource (timeout);
							idle.Client.Idle (timeout.Token, idle.CancellationToken);
							idle.Client.NoOp (idle.CancellationToken);
						} catch (OperationCanceledException) {
							// This means that idle.CancellationToken was cancelled, not the DoneToken nor the timeout
							break;
						} finally {
							// We're about to Dispose() the timeout source, so set it to null.
							idle.SetTimeoutSource (null);
						}
					}
				}
			}
		}
	}
}
