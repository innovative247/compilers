namespace ibsCompiler
{
    public enum SQLServerTypes
    {
        SYBASE = 1,
        MSSQL = 2
    }

    public enum BcpDirection
    {
        IN = 1,
        OUT = 2
    }

    public struct CommandVariables
    {
        private string _server;
        private string _port;
        public string User;
        public string Pass;
        public string Command;
        public SQLServerTypes ServerType;
        public string Database;
        public string Server
        {
            set
            {
                if (value.Contains(':'))
                {
                    _port = value.Substring(value.IndexOf(':'));
                    value = value.Substring(0, value.IndexOf(':'));
                }
                else if (value.Contains(','))
                {
                    _port = value.Substring(value.IndexOf(','));
                    value = value.Substring(0, value.IndexOf(','));
                }
                _server = value.Replace("/", "\\");
            }
            get
            {
                if (_server != null)
                {
                    return _server;
                }
                return "";
            }
        }
        public string Port
        {
            get { return _port ?? ""; }
        }
        public string ServerNameOnly
        {
            get
            {
                string mystr = Server;
                if (Server.Contains(','))
                    mystr = Server.Split(',')[0];
                else if (Server.Contains(':'))
                    mystr = Server.Split(':')[0];
                else if (Server.Contains('\\'))
                    mystr = Server.Split('\\')[0];
                return mystr;
            }
        }
        public string Upgrade_no;
        public string OutFile;
        public bool EchoInput;
        public int SeqFirst;
        public int SeqLast;
        public string Bcp;
        public bool ChangeLog;
        public string CommandName;
        public bool Preview;
    }

    public struct ExecReturn
    {
        public bool Returncode;
        public string Output;
    }
}
