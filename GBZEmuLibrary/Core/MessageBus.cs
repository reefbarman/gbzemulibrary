using System;

namespace GBZEmuLibrary
{
    internal class MessageBus
    {
        private static MessageBus _instance;

        public static MessageBus Instance => _instance ?? (_instance = new MessageBus());

        public Action<Interrupts> OnRequestInterrupt = null;

        public void RequestInterrupt(Interrupts interrupt)
        {
            OnRequestInterrupt?.Invoke(interrupt);
        }
    }
}
