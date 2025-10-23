# Tcp Group Chat
A simple multi-client tcp group chat built using TcpClient and TcpListener from the .NET framework. Multiple clients can connect and send messages to the server which in turn broadcasts them to the viewers. Message framing is handled via a length prefix, the first four bytes after the first byte indicate the length of the payload. 
Application-level keep-alives (heartbeats) are also implemented to detect half-open connections. The type of each message (normal or heartbeat) is indicated by the first byte of the stream.

## Features
There are two types of clients implemented: viewer and messenger: 
Its because a client where writing to the console(received messages) and reading from the console(sending messages) at the same would have been a pain in the ass to implement(not impossible though), and I was mostly interested in the networking side of the project.
The client type is indicated by the first message that they send to the server, the payload is either "viewer" or "messenger".

### Messenger
Each messenger can have a unique username associated with it. Usernames are kept track of on the server side. 
Once a unique username has been chosen the client is allowed to send messages which the server broadcasts to all the viewers.

### Viewer
Viewers receive normal messages along with heartbeats of course. The first normal message received is the chat history: it is sent in a single string where each message is preceded by its length for parsing. 

### Server
It does all of the features mentioned above: handling messengers with unique usernames, handling viewers and sending keep-alives to check half-open connections. If a half open connection is detected(client not responding) it immediately disconnects the client.
Keep alives: sends heartbeat messages (first byte being 1) periodically, at most it allows 3 retries(heartbeats with no response from the client) if the 3rd retry fails the server considers the client dead, it disconnects.

### Protocol summar:
| Bytes   | Description                                  |
| ------- | -------------------------------------------- |
| 1       | Message type (0 = normal, 1 = heartbeat)     |
| 2–5     | Payload length (4 bytes, little-endian)      |
| 6–...   | Message payload                              |

