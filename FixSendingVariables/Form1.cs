using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Management;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace FixSendingVariables
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void Check_Click(object sender, EventArgs e)
        {
            string companyID = compID.Text;
            if (IsNumeric(companyID))
            {
                FixSendingVariables(companyID);
            }
            else
            {
                MessageBox.Show("Sorry, company ID is not valid");
                return;
            }
        }

        public static void FixSendingVariables(string company)
        {
            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder();
            {
                builder.DataSource = "192.168.100.30";
                builder.UserID = "DataAnalyst";
                builder.Password = "DA!!$$[2016]";
                builder.InitialCatalog = "Clickback";

                using (SqlConnection connection = new SqlConnection(builder.ConnectionString))
                {
                    connection.Open();
                    // does company have valid pool info (domains, poolID etc)
                    string poolID = GetPoolID(connection, company);
                    string companyInfo = GetCompanyInfo(company, connection);

                    if (string.IsNullOrEmpty(companyInfo))
                    {
                        MessageBox.Show("Sorry, this CompanyID has no valid pool info.");
                        return;
                    }
                    //GetCleanIPs(connection, poolID, company);
                    //CreateDNSRecord("A", "smtp218.cbsend.com", "148.59.128.218");
                    // check if all domains are razor'd and fix if necessary
                    if (AllDomainsRazored(connection, poolID))
                    {
                        MessageBox.Show("All Domains are razor'd. Will fix...");
                        string updateRazor = "update bounce_addrs set active_flag=1, current_blacklists=''" +
                         " where gen3_vmta_pool =" + poolID;
                        RunUpdateQuery(updateRazor, connection);
                    }


                    if (AllIpsBlacklisted(connection, poolID))
                    {
                        MessageBox.Show("All IPs are Blacklisted.  Will fix...");
                        RemoveIPsFromPool(connection, poolID);
                        // get three clean IPs from pool 121
                        GetCleanIPs(connection, poolID, company);
                    }

                    if (IPsInPool(connection, poolID))
                    {
                        // check whether retry IP is blacklisted 
                        CheckAndFixBlacklistedRetryIP(connection, poolID, company);

                        if (!CorrectLoadBalancingGroup(connection, poolID))
                        {
                            MessageBox.Show("Load Balancing Group Does Not Match IP.");
                        }
                    }
                    connection.Close();
                }
            }
        }

        public static SqlDataReader GetInfoFromQuery(string query, SqlConnection connection)
        {
            SqlCommand command = new SqlCommand(query, connection);
            SqlDataReader reader = command.ExecuteReader();
            return reader;
        }

        public static bool IsListed(string blist)
        {
            if (blist != "")
            {
                return true;
            }
            return false;
        }

        public static bool IsNumeric(string num)
        {
            Regex regex = new Regex(@"\d+");
            if (regex.IsMatch(num))
            {
                return true;
            }
            return false;
        }

        public static bool AllIpsBlacklisted(SqlConnection connection, string poolID)
        {
            int totalIPs = 0;
            List<string> blacklists = new List<string>();
            string getIPs = GetIPsinPool(poolID);
            SqlDataReader reader = GetInfoFromQuery(getIPs, connection);
            while (reader.Read())
            {
                string currentBlacklist = reader[12].ToString();
                totalIPs += 1;
                if (IsListed(currentBlacklist))
                {
                    blacklists.Add(currentBlacklist);
                }
            }
            reader.Close();
            if (blacklists.Count == totalIPs)
            {
                return true;
            }
            return false;
        }

        public static bool AllDomainsRazored(SqlConnection connection, string poolID)
        {
            string getDomains = "select * from rewrite_urls where gen3_vmta_pool=" + poolID;
            SqlDataReader reader = GetInfoFromQuery(getDomains, connection);
            List<string> razorBlacklists = new List<string>();
            int totalDomains = 0;

            while (reader.Read())
            {
                string currentBlacklist = reader[5].ToString();
                totalDomains += 1;
                if (currentBlacklist.Contains("razor"))
                {
                    razorBlacklists.Add(currentBlacklist.ToString());
                }
            }
            reader.Close();

            if (totalDomains == razorBlacklists.Count)
            {
                return true;
            }
            return false;
        }

        public static void CheckAndFixBlacklistedRetryIP(SqlConnection connection, string poolID, string company)
        {
            string IpPurposeAndBlacklist = IpPurposeBlacklistQuery(connection, poolID);

            List<Tuple<string, string, string>> ipInfo = GetIpPurposeInfo(connection, poolID);
            {
                foreach (var inf in ipInfo)
                {
                    string ipID = inf.Item1;
                    string ipPurpose = inf.Item2;
                    string ipBlacklist = inf.Item3;

                    if (IsListed(ipBlacklist) && ipPurpose == "2")
                    {
                        MessageBox.Show("Reply ID is Blacklisted, will fix...");
                        // switch retryIP to Sending IP
                        string query = "update IPPurposes__Clickback_IPs set IPpurposeID=1 where ip_id =" + ipID;
                        RunUpdateQuery(query, connection);
                        // take a sending IP and update to Retry
                        ChangeSendingIpToRetry(connection, poolID);
                    }
                }
                // if no clean sending ips, add one
                if (NoCleanSendingIps(connection, poolID, "1"))
                {
                    NeedNewSendIp(connection, company, poolID, 1);
                }
                // if no clean retry ip, add one
                if (NoCleanSendingIps(connection, poolID, "2"))
                {
                    NeedNewSendIp(connection, company, poolID, 2);
                }
            }   
        }

        public static void ChangeSendingIpToRetry(SqlConnection connection, string poolID)
        {
            string IpPurposeAndBlacklist = IpPurposeBlacklistQuery(connection, poolID);
            List<string> ipToBeChanged = new List<string>();
            List<Tuple<string, string, string>> ipInfo = GetIpPurposeInfo(connection, poolID);
            foreach (var inf in ipInfo)
            {
                string ipID = inf.Item1;
                string ipPurpose = inf.Item2;
                string ipBlacklist = inf.Item3;

                if (!IsListed(ipBlacklist) && ipPurpose == "1" && ipToBeChanged.Count == 0)
                {
                    ipToBeChanged.Add(ipID);
                    string changeToRetry = "update IPPurposes__Clickback_IPs set IPpurposeID=2 where ip_id =" + ipID;
                    RunUpdateQuery(changeToRetry, connection);
                }
            }
        }

        public static bool CorrectLoadBalancingGroup(SqlConnection connection, string poolID)
        {
            string checkLoadBalancingGroup = "select distinct clickback_ips.public_ip, companies.LoadBalancingGroup" +
                " from clickback_ips inner join companies on companies.gen3_pool = " +
                "clickback_ips.Gen3_VMTA_Pool where companies.gen3_pool = " + poolID;
            SqlDataReader reader = GetInfoFromQuery(checkLoadBalancingGroup, connection);

            while (reader.Read())
            {
                string publicIP = reader[0].ToString();
                string lbg = reader[1].ToString();

                if ((publicIP.StartsWith("185") && lbg == "2") ||
                    (!publicIP.StartsWith("185") && lbg == "0"))
                {
                    return true;
                }
            }
            reader.Close();
            return false;
        }

        public static void RunUpdateQuery(string query, SqlConnection connection)
        {
            SqlCommand command = new SqlCommand(query, connection);
            command.ExecuteNonQuery();
        }

        public static string GetCompanyInfo(string company, SqlConnection connection)
        {
            string getInfo = "select company_name, gen3_pool, companyID from companies where companyID=" + company;
            SqlDataReader reader = GetInfoFromQuery(getInfo, connection);
            string result = "";

            while (reader.Read())
            {
                string poolID = reader[1].ToString();
                result = "select * from rewrite_urls where gen3_vmta_pool=" + poolID;
            }
            reader.Close();
            return result;
        }

        public static string GetPoolID(SqlConnection connection, string company)
        {
            string getPoolID = "select gen3_pool from companies where companyID=" + company;
            string poolID = "";
            SqlDataReader reader = GetInfoFromQuery(getPoolID, connection);
            while (reader.Read())
            {
                poolID = reader[0].ToString();
            }
            reader.Close();
            return poolID;
        }

        public static string GetIPsinPool(string poolID)
        {
            return "select ''''+public_ip+''',',  * from clickback_ips where Gen3_VMTA_Pool=" + poolID;
        }

        public static void RemoveIPsFromPool(SqlConnection connection, string poolID)
        {
            string getListOfIPs = GetIPsinPool(poolID);
            List<string> ipsToBeRemoved = GetIPsAndAddToList(connection, getListOfIPs, 2);

            foreach (string oldIp in ipsToBeRemoved)
            {
                string moveIpToPool = "update clickback_ips set Gen3_VMTA_Pool=121 where public_ip='" + oldIp + "'";
                RunUpdateQuery(moveIpToPool, connection);
            }
        }

        public static void GetCleanIPs(SqlConnection connection, string poolID, string companyID)
        {
            if (NoIPsInPool(connection, poolID))
            {
                string goodIPs = "";
                if (LocalServer(connection, companyID))
                {
                     goodIPs = "select top 3 public_ip from clickback_ips where status=2 and Gen3_VMTA_Pool = 121 and public_ip like '216.119%' and public_ip not like '216.119.192%'";
                }
                else
                {
                    goodIPs = "select top 3 public_ip from clickback_ips where status=2 and Gen3_VMTA_Pool = 121 and public_ip like '185.227.50%'";
                }



                
                List<string> listOfGoodIPs = GetIPsAndAddToList(connection, goodIPs, 0);

                if (listOfGoodIPs.Count == 3)
                {
                    //add each ip to applicable pool
                    foreach (string goodIP in listOfGoodIPs)
                    {
                        string rdnsDomain = GetRDNSDomain(connection, goodIP, companyID);

                        if (rdnsDomain == "")
                        {
                            MessageBox.Show("Client Does Not Have Active RDNS Domain.  Please Fix.");
                            return;
                        }

                        string updatePool = string.Format("update clickback_ips set Gen3_VMTA_Pool={0}, domain_name='{1}' where public_ip='" + goodIP + "'", poolID, rdnsDomain);
                        RunUpdateQuery(updatePool, connection);
                        // update ptr record to point to correct domain in DNS

                        // check RDNS forward is correct
                        if (!RDNSConfirmed(goodIP))
                        {
                            MessageBox.Show(string.Format("RDNS not configured for {0}", goodIP), "Oops");
                        }
                    }
                    MessageBox.Show("Don't forget to check Reverse DNS!");
                    // setup send and retry IPs
                    SetSendAndRetryIP(connection, poolID);
                }
                else
                {
                    MessageBox.Show("Sorry, need to add more IPs from Source Pool.");
                    return;
                }
            }
                
        }

        public static List<string> GetIPsAndAddToList(SqlConnection connection, string query, int index)
        {
            string ip = "";
            SqlDataReader reader = GetInfoFromQuery(query, connection);
            List<string> listOfIPs = new List<string>();
            while (reader.Read())
            {
                ip = reader[index].ToString();
                listOfIPs.Add(ip);
            }
            reader.Close();
            return listOfIPs;
        }

        public static List<Tuple<string, string, string>> GetIpPurposeInfo(SqlConnection connection, string poolID)
        {
            string IpPurposeAndBlacklist = IpPurposeBlacklistQuery(connection, poolID);
            SqlDataReader reader = GetInfoFromQuery(IpPurposeAndBlacklist, connection);
            List<Tuple<string, string, string>> ipInfo = new List<Tuple<string, string, string>>();
            while (reader.Read())
            {
                string ipID = reader[0].ToString();
                string ipPurpose = reader[1].ToString();
                string ipBlacklist = reader[2].ToString();
                ipInfo.Add(new Tuple<string, string, string>(ipID, ipPurpose, ipBlacklist));
            }
            reader.Close();
            return ipInfo;
        }

        public static string IpPurposeBlacklistQuery(SqlConnection connection, string poolID)
        {
            return "select clickback_ips.ip_id, ipPurposeID, Current_Blacklists from clickback_ips " +
                        "inner join IPPurposes__Clickback_IPs on IPPurposes__Clickback_IPs.ip_id = Clickback_IPs.IP_ID where Gen3_VMTA_Pool = " + poolID;
        }

        public static string GetRDNSDomain(SqlConnection connection, string ipAddress, string company)
        {
            // get company RDNS domain
            string getDomain = string.Format("select * from domains where companyID={0} and domain_name like '%rdns%' and master_pool != 56", company);
            SqlDataReader reader = GetInfoFromQuery(getDomain, connection);
            string domain = "";
            while (reader.Read())
            {
                domain = reader[1].ToString();
            }
            reader.Close();

            //get last octet of ip address
            int number = GetLengthofLastOctet(ipAddress);
            string ipPrefix = ipAddress.Substring(ipAddress.Length - number);
            if (domain != "")
            {
                return "smtp" + ipPrefix + "." + domain;
            }
            return "";
            
        }

        public static int GetLengthofLastOctet(string address)
        {
            int number = address.LastIndexOf(".");
            // ignore last character of ip address string (")
            return address.Length - (number + 1);
        }

        public static void SetSendAndRetryIP(SqlConnection connection, string poolID)
        {
            List<Tuple<string, string, string>> info = GetIpPurposeInfo(connection, poolID);
            int sendIPCount = 0;
            string query = "";
            foreach (var inf in info)
            {
                string ipID = inf.Item1;
                sendIPCount += 1;

                if (sendIPCount <= 2)
                {
                    query = "update IPPurposes__Clickback_IPs set IPpurposeID=1 where ip_id =" + ipID;
                }
                else
                {
                    query = "update IPPurposes__Clickback_IPs set IPpurposeID=2 where ip_id =" + ipID;
                }
                RunUpdateQuery(query, connection);
            }
        }

        public static bool NoIPsInPool(SqlConnection connection, string poolID)
        {
            string ipsInPool = GetIPsinPool(poolID);
            List<string> ipList = GetIPsAndAddToList(connection, ipsInPool, 2);
            if (ipList.Count == 0)
            {
                return true;
            }
            return false;
        }

        public static bool RDNSConfirmed(string ip)
        {
            string ipResult = "";
            try
            {
                IPHostEntry value = Dns.GetHostEntry(ip);
                string domain = value.HostName;
                IPHostEntry getIp = Dns.GetHostEntry(domain);
                ipResult = getIp.AddressList[0].ToString();

                if (ipResult == ip)
                {
                    return true;
                }
            }
            catch
            {
                MessageBox.Show("Domain not properly configured.  Check NameServer in Dynadot");
            }

            return false;
        }

        public static string GetArpaAddress(string ip)
        {
            string[] octetList = ip.Split('.');
            string result = "";
            for (int i = octetList.Length - 1; i >= 0; i--)
            {
                result = result += octetList[i] + ".";
            }
            return result + "in-addr.arpa";
        }

        public static void NeedNewSendIp(SqlConnection connection, string compID, string poolID, int num)
        {
            string query = "select top 1 public_ip from clickback_ips where status=2 and Gen3_VMTA_Pool = 121 and public_ip like '216%'";
            List<string> newIp = GetIPsAndAddToList(connection, query, 0);
            string ip = newIp[0];
            string rdnsDomain = GetRDNSDomain(connection, ip, compID);
            string updatePool = string.Format("update clickback_ips set Gen3_VMTA_Pool={0}, domain_name='{1}' where public_ip='" + ip + "'", poolID, rdnsDomain);
            RunUpdateQuery(updatePool, connection);
            // get ipID of ip
            string ipID = GetIpID(connection, ip, poolID);
            // setup ip as sending                             
            string updateToSend = string.Format("update IPPurposes__Clickback_IPs set IPpurposeID={0} where ip_id =" + ipID, num);
            RunUpdateQuery(updateToSend, connection);
        }

        public static string GetIpID(SqlConnection connection, string ip, string poolID)
        {
            string result = "";
            string getip_ID = string.Format("select distinct public_IP, clickback_ips.ip_ID from IPPurposes__Clickback_IPs " +
                "inner join clickback_ips on clickback_ips.ip_ID = clickback_ips.ip_ID where Gen3_VMTA_Pool = {0} " +
                "and public_ip='" + ip + "'", poolID);
            SqlDataReader reader = GetInfoFromQuery(getip_ID, connection);
            while (reader.Read())
            {
                result = reader[1].ToString();
            }
            reader.Close();
            return result;
        }

        public static bool NoCleanSendingIps(SqlConnection connection, string poolID, string purpose)
        {
            int cleanIpCount = 0;
            string query = IpPurposeBlacklistQuery(connection, poolID);
            SqlDataReader reader = GetInfoFromQuery(query, connection);
            while (reader.Read())
            {
                string ipPurposeID = reader[1].ToString();
                string currentBlacklist = reader[2].ToString();
                if (!IsListed(currentBlacklist) && ipPurposeID == purpose)
                {
                    cleanIpCount += 1;
                }
            }
            reader.Close();
            if (cleanIpCount == 0)
            {
                return true;
            }
            return false;
        }

        public static bool LocalServer(SqlConnection connection, string compID)
        {
            string query = "select LoadBalancingGroup from companies where companyid=" + compID;
            string lbg = "";
            SqlDataReader reader = GetInfoFromQuery(query, connection);
            while (reader.Read())
            {
                lbg = reader[0].ToString();
                if (lbg == "0")
                {
                    reader.Close();
                    return true;
                }
            }
            reader.Close();
            return false; 
        }

        public static bool IPsInPool(SqlConnection connection, string poolID)
        {
            List<Tuple<string, string, string>> ipInfo = GetIpPurposeInfo(connection, poolID);
            if (ipInfo.Count > 0)
            {
                return true;
            }
            return false;
        }

        //public static void AddPtrRecord(string reverseIP, string domain)
        //{
        //    HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create("http://192.168.100.101:8053/zones/" + reverseIP + "/records");
        //    httpWebRequest.ContentType = "application/json";
        //    httpWebRequest.Method = "PATCH";
        //    //httpWebRequest.KeepAlive = false;
            
        //    using (StreamWriter sw = new StreamWriter(httpWebRequest.GetRequestStream()))
        //    {
        //        string json = "[{ \"Name\":\"" + reverseIP + "\"," +
        //                        "\"Type\":\"PTR\"," +
        //                        "\"TTL\":3600," +
        //                        "\"Data\":\"" + domain + ".\"," +
        //                      "\"Comment\":\"Test\"}]";
        //        sw.Write(json);
        //    }
        //    // show the last SOA update date in DNS
        //    HttpWebResponse httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
        //    using (StreamReader reader = new StreamReader(httpResponse.GetResponseStream()))
        //    {
        //        string result = reader.ReadToEnd();
        //    }
        //}

        public static void CreateDNSRecord(string recordType, string name, string data)
        {
            HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create("http://192.168.100.101:8053/zones/" + name + "/records");
            httpWebRequest.ContentType = "application/json";
            httpWebRequest.Method = "PATCH";
            httpWebRequest.KeepAlive = false;
            
            using (StreamWriter sw = new StreamWriter(httpWebRequest.GetRequestStream()))
            {
                string json = string.Format("[{{ \"Name\":\"" + name + "\"," +
                                "\"Type\":\"{0}\"," +
                                "\"TTL\":3600," +
                                "\"Data\":\"" + data + ".\"," +
                              "\"Comment\":\"Test\"}}]", recordType);
                sw.Write(json);
            }
            HttpWebResponse httpResponse = (HttpWebResponse)httpWebRequest.GetResponse();
            using (StreamReader reader = new StreamReader(httpResponse.GetResponseStream()))
            {
                string result = reader.ReadToEnd();
            }
        }
    }
}
