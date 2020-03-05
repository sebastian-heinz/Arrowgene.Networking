/*
 * MIT License
 * 
 * Copyright (c) 2018 Sebastian Heinz <sebastian.heinz.gt@googlemail.com>
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */


using System.Collections.Concurrent;

namespace Arrowgene.Networking.Tcp.Consumer.BlockingQueueConsumption
{
    public class BlockingQueueConsumer : IConsumer
    {
        public BlockingCollection<ClientEvent> ClientEvents;

        public void OnStart()
        {
            ClientEvents = new BlockingCollection<ClientEvent>();
        }

        public void OnStarted()
        {
        }

        public void OnReceivedData(ITcpSocket socket, byte[] data)
        {
            ClientEvents.Add(new ClientEvent(socket, ClientEventType.ReceivedData, data));
        }

        public void OnClientDisconnected(ITcpSocket socket)
        {
            ClientEvents.Add(new ClientEvent(socket, ClientEventType.Disconnected));
        }

        public void OnClientConnected(ITcpSocket socket)
        {
            ClientEvents.Add(new ClientEvent(socket, ClientEventType.Connected));
        }

        public void OnStop()
        {
        }

        public void OnStopped()
        {
        }
    }
}