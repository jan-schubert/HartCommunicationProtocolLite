﻿using System;
using System.IO.Ports;
using System.Threading;
using log4net;

namespace Finaltec.Communication.HartLite
{
    public class HartCommunicationLite
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(HartCommunicationLite));
        private readonly SerialPort _port;
        private readonly HartCommandParser _parser = new HartCommandParser();
        private AutoResetEvent _waitForResponse;
        private CommandResult _lastReceivedCommand;
        private byte[] _currentAddress;
        private event ReceiveHandler Receive;
        private bool _zeroCommandExecuted;
        private int _numberOfRetries;

        private const double ADDITIONAL_WAIT_TIME_BEFORE_SEND = 5.0;
        private const double ADDITIONAL_WAIT_TIME_AFTER_SEND = 50.0;
        private const double REQUIRED_TRANSMISSION_TIME_FOR_BYTE = 9.1525;

        /// <summary>
        /// Gets or sets the length of the preamble.
        /// </summary>
        /// <value>The length of the preamble.</value>
        public int PreambleLength { get; set; }
        /// <summary>
        /// Gets or sets the max number of retries.
        /// </summary>
        /// <value>The max number of retries.</value>
        public int MaxNumberOfRetries { get; set; }
        /// <summary>
        /// Gets or sets the timeout.
        /// </summary>
        /// <value>The timeout.</value>
        public TimeSpan Timeout { get; set; }
        public bool AutomaticZeroCommand { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="HartCommunicationLite"/> class.
        /// </summary>
        /// <param name="comPort">The COM port.</param>
        public HartCommunicationLite(string comPort) : this(comPort, 2)
        {}

        /// <summary>
        /// Initializes a new instance of the <see cref="HartCommunicationLite"/> class.
        /// </summary>
        /// <param name="comPort">The COM port.</param>
        /// <param name="maxNumberOfRetries">The max number of retries.</param>
        public HartCommunicationLite(string comPort, int maxNumberOfRetries)
        {
            MaxNumberOfRetries = maxNumberOfRetries;
            PreambleLength = 10;
            Timeout = TimeSpan.FromSeconds(4);
            AutomaticZeroCommand = true;

            _port = new SerialPort(comPort, 1200, Parity.Odd, 8, StopBits.One);
        }

        /// <summary>
        /// Gets the port.
        /// </summary>
        /// <value>The port.</value>
        public SerialPort Port
        {
            get { return _port; }
        }

        public OpenResult Open()
        {
            try
            {
                _port.RtsEnable = false;
                _port.DtrEnable = true;

                _parser.CommandComplete += CommandComplete;

                _port.DataReceived += DataReceived;
                _port.Open();
                return OpenResult.Opened;
            }
            catch (ArgumentException exception)
            {
                _port.DataReceived -= DataReceived;
                Log.Warn("Cannot open port.", exception);
                return OpenResult.ComPortNotExisting;
            }
            catch (UnauthorizedAccessException exception)
            {
                _port.DataReceived -= DataReceived;
                Log.Warn("Cannot open port.", exception);
                return OpenResult.ComPortIsOpenAlreadyOpen;
            }
            catch (Exception exception)
            {
                _port.DataReceived -= DataReceived;
                Log.Warn("Cannot open port.", exception);
                return OpenResult.UnknownComPortError;
            }
        }

        public CloseResult Close()
        {
            try
            {
                _parser.CommandComplete -= CommandComplete;
                _port.DataReceived -= DataReceived;
                _port.Close();
                return CloseResult.Closed;
            }
            catch (InvalidOperationException exception)
            {
                Log.Warn("Cannot close port.", exception);
                return CloseResult.PortIsNotOpen;
            }
        }

        public CommandResult Send(byte command)
        {
            return Send(command, new byte[0]);
        }

        public CommandResult Send(byte command, byte[] data)
        {
            if (AutomaticZeroCommand && command != 0 && !_zeroCommandExecuted)
                SendZeroCommand();

            _numberOfRetries = MaxNumberOfRetries;
            return ExecuteCommand(new Command(PreambleLength, _currentAddress, command, new byte[0], data));
        }

        public CommandResult SendZeroCommand()
        {
            _numberOfRetries = MaxNumberOfRetries;
            return ExecuteCommand(Command.Zero(PreambleLength));
        }

        private CommandResult ExecuteCommand(Command requestCommand)
        {
            Receive += CommandReceived;
            try
            {
                SendCommand(requestCommand);
                if (!_waitForResponse.WaitOne(Timeout))
                {
                    Receive -= CommandReceived;

                    if (ShouldRetry())
                        return ExecuteCommand(requestCommand);
                    return null;
                }

                Receive -= CommandReceived;

                if(HasCommunicationError())
                    return ShouldRetry() ? ExecuteCommand(requestCommand) : _lastReceivedCommand;

                return _lastReceivedCommand;
            }
            catch (Exception)
            {
                Receive -= CommandReceived;

                if (ShouldRetry())
                    return ExecuteCommand(requestCommand);

                return null;
            }
        }

        private bool HasCommunicationError()
        {
            if (_lastReceivedCommand.ResponseCode.FirstByte < 128)
                return false;

            Log.Warn("Communication error. First bit of response code byte is set.");

            if ((_lastReceivedCommand.ResponseCode.FirstByte & 0x40) == 0x40)
                Log.WarnFormat("Vertical Parity Error - The parity of one or more of the bytes received by the device was not odd.");
            if ((_lastReceivedCommand.ResponseCode.FirstByte & 0x20) == 0x20)
                Log.WarnFormat("Overrun Error - At least one byte of data in the receive buffer of the UART was overwritten before it was read (i.e., the slave did not process incoming byte fast enough).");
            if ((_lastReceivedCommand.ResponseCode.FirstByte & 0x10) == 0x10)
                Log.WarnFormat("Framing Error - The Stop Bit of one or more bytes received by the device was not detected by the UART (i.e. a mark or 1 was not detected when a Stop Bit should have occoured)");
            if ((_lastReceivedCommand.ResponseCode.FirstByte & 0x08) == 0x08)
                Log.WarnFormat("Longitudinal Partity Error - The Longitudinal Partity calculated by the device did not match the Check Byte at the end of the message.");
            if ((_lastReceivedCommand.ResponseCode.FirstByte & 0x02) == 0x02)
                Log.WarnFormat("Buffer Overflow - The message was too long for the receive buffer of the device.");

            return true;
        }

        private bool ShouldRetry()
        {
            return _numberOfRetries-- > 0;
        }

        private void SendCommand(Command command)
        {
            _waitForResponse = new AutoResetEvent(false);
            _parser.Reset();

            byte[] bytesToSend = command.ToByteArray();

            Thread.Sleep(100);

            _port.DtrEnable = false;
            _port.RtsEnable = true;

            Thread.Sleep(Convert.ToInt32(ADDITIONAL_WAIT_TIME_BEFORE_SEND));

            DateTime startTime = DateTime.Now;

            Log.Debug(string.Format("Data sent to {1}: {0}", BitConverter.ToString(bytesToSend), _port.PortName));
            _port.Write(bytesToSend, 0, bytesToSend.Length);

            SleepAfterSend(bytesToSend.Length, startTime);
            _port.RtsEnable = false;
            _port.DtrEnable = true;
        }

        private void CommandReceived(object sender, CommandResult args)
        {
            _lastReceivedCommand = args;
            _waitForResponse.Set();
        }

        private void DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            byte[] receivedBytes = new byte[_port.BytesToRead];
            _port.Read(receivedBytes, 0, receivedBytes.Length);
            Log.Debug(string.Format("Received data from {1}: {0}", BitConverter.ToString(receivedBytes), _port.PortName));

            _parser.ParseNextBytes(receivedBytes);
        }

        private static void SleepAfterSend(int dataLength, DateTime startTime)
        {
            TimeSpan waitTime = CalculateWaitTime(dataLength, startTime);

            if (waitTime.Milliseconds > 0)
                Thread.Sleep(waitTime);
        }

        private static TimeSpan CalculateWaitTime(int dataLength, DateTime startTime)
        {
            TimeSpan requiredTransmissionTime = TimeSpan.FromMilliseconds(Convert.ToInt32(REQUIRED_TRANSMISSION_TIME_FOR_BYTE * dataLength + ADDITIONAL_WAIT_TIME_AFTER_SEND));
            return startTime + requiredTransmissionTime - DateTime.Now;
        }

        private void CommandComplete(Command command)
        {
            if(command.CommandNumber == 0)
            {
                //PreambleLength = command.PreambleLength;

                _currentAddress = new byte[5];
                _currentAddress[0] = (byte)(command.Data[1] | 0x80);
                _currentAddress[1] = command.Data[2];
                _currentAddress[2] = command.Data[9];
                _currentAddress[3] = command.Data[10];
                _currentAddress[4] = command.Data[11];

                _zeroCommandExecuted = true;
            }

            if (Receive != null)
                Receive(this, new CommandResult(command));
        }
    }
}
