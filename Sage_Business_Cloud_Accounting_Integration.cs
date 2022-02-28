using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.UI.WebControls;

namespace AFSI
{
    public class Sage_Business_Cloud_Accounting_Integration
    {
        private HttpClient httpClient = new HttpClient();
        private string targetURL;
        private string loginCredentials = string.Empty;
        private string apiKey = string.Empty;
        private int statusCode = 0;
        private string responseContent = string.Empty;
        private IList<Item> invoiceItems = new List<Item>();
        private int sageCustomerId = -1;
        private string name = string.Empty, dealerCustomerCode = string.Empty, ADTcustomerCode = string.Empty;
        private int taxTypeIdVAT = 0;
        private int taxTypeIdNoVAT = 0;
        private int paymentMethodId = 0;
        private int receiptId = 0;
        private int companyId = 0;
        
        public int userId;
        public int clientId;
        public double balance = 0;
        public string requestType = string.Empty;
        public string jobType = string.Empty;
        public int invoiceId = 0;
        public double invoiceAmount;
        public double prorata;
        public double radioFee;
        public string paymentMethod = string.Empty;
        public int bankAccountId;
        public int paymentId = 0;
        public double receiptAmount = 0;
        public DateTime date;
        public int allocationId = 0;
        public List<Allocation> allocationList;

        private void SageAuthentication()
        {
            clsDataBase clsDb = new clsDataBase();
            SqlDataReader sdr = clsDb.GetDataWithReader(@"SELECT AccountingSoftwareApiUrl, ltrim(rtrim(AccountingSoftwareAPIkey)) AS AccountingSoftwareAPIkey, AccountingSoftwareUsername + ':' + AccountingSoftwarePassword AS Credentials FROM Settings");

            while (sdr.Read())
            {
                targetURL = sdr["AccountingSoftwareApiUrl"].ToString();
                apiKey = sdr["AccountingSoftwareAPIkey"].ToString();
                loginCredentials = sdr["Credentials"].ToString();
            }

            if (sdr != null)
                sdr.Close();
                        
            clsDb.CloseDBConnection();

            var authenticationByteArray = Encoding.ASCII.GetBytes(loginCredentials);
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(authenticationByteArray));
        }

        public void InsertInvoice()
        {
            SageAuthentication();

            if (!string.IsNullOrEmpty(targetURL) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(loginCredentials))
            {
                requestType = "Insert Invoice";
                GetClientDetail();
            }
            else
            {
                clsDataBase clsDb = new clsDataBase();
                clsDb.NonQuery($@"insert into [Sage Integration Event Log] (Date, ClientId, Description, UserId) values (getdate(), {clientId}, 
                                    'Target URL and/or api key and/or username and/or password is missing', {userId})");
            }
        }

        protected void GetClientDetail()
        {
            clsDataBase clsDb = new clsDataBase();
            SqlDataReader sdr = clsDb.GetDataWithReader($@"select sTitle, sFirstNames, sSurnameCompanyName, sDealerAgreementNo, sCustomerCode 
                                                            from agreements 
                                                            where pkiAgreementId = {clientId}");

            while (sdr.Read())
            {
                if (!string.IsNullOrEmpty(sdr["sSurnameCompanyName"].ToString()))
                    name = sdr["sSurnameCompanyName"].ToString();

                if (!string.IsNullOrEmpty(sdr["sFirstNames"].ToString()))
                    name += " " + sdr["sFirstNames"].ToString();

                if (!string.IsNullOrEmpty(sdr["sTitle"].ToString()))
                    name += " " + sdr["sTitle"].ToString();

                dealerCustomerCode = sdr["sDealerAgreementNo"].ToString();
                ADTcustomerCode = sdr["sCustomerCode"].ToString();
            }

            if (sdr != null)
                sdr.Close();

            clsDb.CloseDBConnection();

            GetCompanyId();
        }

        private void Log(string sageRequest, int statusCode, string reasonPhrase, string description)
        {
            clsDataBase clsDb = new clsDataBase();
            clsDb.NonQuery($@"INSERT INTO [Sage Integration Event Log] (SageRequest, SageResponseCode, SageResponseContent, Description, Date, ClientId, UserId) 
                                        VALUES ('{sageRequest}', {statusCode}, '{reasonPhrase}', '{description}', GETDATE(), {clientId}, {userId})");
        }

        public async void GetCompanyId()
        {
            string companiesJson = string.Empty;

            try
            {
                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.GetAsync($"{targetURL}Company/Get?apikey={apiKey}");
                    Log("Company/Get", (int)response.StatusCode, response.ReasonPhrase,"");
                    companiesJson = await response.Content.ReadAsStringAsync();
                }

                JObject companiesObject = JObject.Parse(companiesJson);
                IList<JToken> companiesJToken = companiesObject["Results"].Children().ToList();
                IList<Company> companiesList = new List<Company>();

                foreach (JToken result in companiesJToken)
                {
                    Company company = result.ToObject<Company>();
                    companiesList.Add(company);
                }

                if (companiesList.Count > 0)
                {
                    companyId = companiesList[0].Id;

                    switch (requestType)
                    {
                        case "Insert Invoice":
                            GetTaxTypeId();
                            break;
                        case "Get Bank Accounts":
                            GetBankAccounts();
                            break;
                        case "Insert Receipt":
                        case "Insert Write Off":
                        case "Insert Credit Note":
                            GetCustomerId();
                            break;
                        case "Allocate":
                            _Allocate();
                            break;
                        case "Delete Allocation":
                            _DeleteAllocation();
                            break;
                        case "Delete Receipt":
                            GetReceiptId();
                            break;
                    }
                }
                else
                {
                    Log("Company/Get", - 1, "", "No company setup in Sage account");
                }
            }
            catch (Exception ex)
            {
                Log("Company/Get", -1, "", ex.Message);
            }
        }

        protected async void GetTaxTypeId()
        {
            string taxTypesJson = string.Empty;

            try
            {
                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.GetAsync($"{targetURL}TaxType/Get?apikey={apiKey}&CompanyId={companyId}");
                    Log("TaxType/Get", (int)response.StatusCode, response.ReasonPhrase, "");
                    taxTypesJson = await response.Content.ReadAsStringAsync();
                }

                JObject taxTypesObject = JObject.Parse(taxTypesJson);
                IList<JToken> taxTypesJToken = taxTypesObject["Results"].Children().ToList();
                IList<TaxType> taxTypesList = new List<TaxType>();

                foreach (JToken result in taxTypesJToken)
                {
                    TaxType taxType = result.ToObject<TaxType>();
                    taxTypesList.Add(taxType);
                }

                for (int i = 0; i < taxTypesList.Count; i++)
                {
                    if (taxTypesList[i].Name == "Standard Rate")
                        taxTypeIdVAT = taxTypesList[i].Id;

                    if (taxTypesList[i].Name == "")
                        taxTypeIdNoVAT = taxTypesList[i].Id;
                }

                GetCustomerId();
            }
            catch (Exception ex)
            {
                Log("TaxType/Get", -1, "", ex.Message);
            }
        }

        protected async void GetCustomerId()
        {
            clsDataBase clsDb = new clsDataBase();
            string clientCode = clsDb.GetScalar($"select sDealerAgreementNo from agreements where pkiAgreementId = {clientId}");
            clsDb.CloseDBConnection();
            string customerJson = string.Empty;

            try
            {
                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.GetAsync($"{targetURL}Customer/Get?apikey={apiKey}&CompanyId={companyId}&$filter=indexof(TextField1, '{clientCode}') ge 0");
                    Log("Customer/Get", (int)response.StatusCode, response.ReasonPhrase, "");
                    customerJson = await response.Content.ReadAsStringAsync();
                }

                JObject customerObject = JObject.Parse(customerJson);
                IList<JToken> customerJToken = customerObject["Results"].Children().ToList();

                sageCustomerId = 0;

                if (customerJToken.Count > 0)
                {
                    IList<SageId> customerList = new List<SageId>();
                    foreach (JToken result in customerJToken)
                    {
                        SageId sageId = result.ToObject<SageId>();
                        customerList.Add(sageId);
                    }

                    sageCustomerId = customerList[0].ID;
                }

                if (sageCustomerId != 0)
                {
                    switch (requestType)
                    {
                        case "Insert Invoice":
                            GetItems();
                            break;
                        case "Insert Receipt":
                            _InsertReceipt();
                            break;
                        case "Insert Write Off":
                            _InsertReceipt();
                            break;
                        case "Insert Credit Note":
                            GetItems();
                            break;
                        case "Allocate":
                            _Allocate();
                            break;
                    }
                }
                else
                {
                    if (requestType == "Insert Invoice" || requestType == "Insert Receipt")
                        InsertClient();
                    else
                        Log("Customer/Get", -1, "", "The " + requestType + " action could not execute because this client could not be located in Sage account");
                }
            }
            catch (Exception ex)
            {
                Log("Customer/Get", -1, "", ex.Message);
            }
        }

        enum StatusCodes
        {
            successfulInsert = 201,
            customerNameMustBeUnique = 400,
            notFound = 404
        }

        protected async void InsertClient()
        {
            statusCode = (int)StatusCodes.customerNameMustBeUnique;
            int sageCustomerNameCount = 1;
            int nameLength = name.Length;
            string stringContent = string.Empty;

            try
            {
                while (statusCode == (int)StatusCodes.customerNameMustBeUnique)
                {
                    if (sageCustomerNameCount > 1)
                    {
                        name = name.Substring(0, nameLength);
                        name += " " + sageCustomerNameCount;
                    }

                    stringContent = @"{
                                  'Name': '" + name + @"',
                                  'Active': true,
                                  'Balance': 0,
                                  'AutoAllocateToOldestInvoice': true,
                                  'TextField1': '" + dealerCustomerCode + @"',
                                  'TextField2': '" + ADTcustomerCode + @"',
                                  'DefaultTaxTypeId': " + taxTypeIdVAT + @"
                                }";

                    using (var httpClient = new HttpClient())
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        HttpResponseMessage response = await httpClient.PostAsync($@"{targetURL}Customer/Save?apikey={apiKey}&CompanyId={companyId}", 
                                                                                        new StringContent(stringContent, Encoding.UTF8, "application/json"));
                        Log("Customer/Save", (int)response.StatusCode, response.ReasonPhrase, "");
                        responseContent = await response.Content.ReadAsStringAsync();
                        statusCode = (int)response.StatusCode;
                        sageCustomerNameCount++;
                    }
                }

                if (statusCode == (int)StatusCodes.successfulInsert)
                {
                    SageId id = JsonConvert.DeserializeObject<SageId>(responseContent);
                    sageCustomerId = id.ID;

                    if (requestType == "Insert Invoice")
                        GetItems();
                    else
                        _InsertReceipt();
                }
            }
            catch (Exception ex)
            {
                Log("Customer/Save", -1, "", ex.Message);
            }
        }

        protected async void GetItems()
        {
            try
            {
                string invoiceItemsJson = string.Empty;

                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.GetAsync($@"{targetURL}Item/Get?includeAdditionalItemPrices=false&includeAttachments=false
                                                                            &apikey={apiKey}&CompanyId={companyId}");
                    Log("Item/Get", (int)response.StatusCode, response.ReasonPhrase, "");
                    invoiceItemsJson = await response.Content.ReadAsStringAsync();
                }

                JObject invoiceItemsObject = JObject.Parse(invoiceItemsJson);
                IList<JToken> invoiceItemsList = invoiceItemsObject["Results"].Children().ToList();

                foreach (JToken result in invoiceItemsList)
                {
                    Item item = result.ToObject<Item>();
                    invoiceItems.Add(item);
                }

                if (invoiceItems.Count > 0)
                {
                    switch (requestType)
                    {
                        case "Insert Invoice":
                            GetItemPrices();
                            break;
                        case "Insert Credit Note":
                            _InsertReceipt();
                            break;
                    }
                }
                else
                    Log("Item/Get", -1, "", "No invoice items setup in Sage account" + userId);
            }
            catch (Exception ex)
            {
                Log("Item/Get", -1, "", ex.Message);
            }
        }

        protected void GetItemPrices()
        {
            double sales = 0;

            if (jobType == "Installation Invoice")
                sales = invoiceAmount - radioFee - prorata;
            else
                sales = invoiceAmount;

            for (int i = 0; i < invoiceItems.Count; i++)
            {
                if (invoiceItems[i].Description == "Sales" || invoiceItems[i].Description == "Services" || invoiceItems[i].Description == "Add-ons")
                    invoiceItems[i].UnitPrice = sales;

                if (invoiceItems[i].Description == "ADT Pro Rata")
                    invoiceItems[i].UnitPrice = prorata;

                if (invoiceItems[i].Description == "ADT Annual Radio License Fee")
                    invoiceItems[i].UnitPrice = radioFee;
            }

            _InsertInvoice();
        }

        protected async void _InsertInvoice()
        {
            try
            {
                clsDataBase clsDb = new clsDataBase();
                bool vat = bool.Parse(clsDb.GetScalar("select SageRadioFeeAndProrataVATincl from Settings"));
                clsDb.CloseDBConnection();

                int taxTypeId = vat ? taxTypeIdVAT : taxTypeIdNoVAT;

                StringBuilder stringContent = new StringBuilder();
                stringContent.Append(@"{
                                  'DueDate': '" + DateTime.Now.ToString("yyyy-MM-dd") + @"',
                                  'CustomerId': " + sageCustomerId + @",
                                  'Date': '" + DateTime.Now.ToString("yyyy-MM-dd") + @"',
                                  'Inclusive': true,
                                  'Reference': '" + invoiceId + @"',
                                  'Lines': [");

                if (jobType == "Installation Invoice")
                {
                    for (int i = 0; i < invoiceItems.Count; i++)
                    {
                        if (invoiceItems[i].Description == "Sales")
                        {
                            stringContent.Append(@"
                                {
                                    'SelectionId': " + invoiceItems[i].ID + @",
                                    'Quantity': 1,
                                    'UnitPriceInclusive': " + invoiceItems[i].UnitPrice + @",
                                    'TaxTypeId': " + taxTypeIdVAT + @"
                                },");
                        }
                    }

                    for (int i = 0; i < invoiceItems.Count; i++)
                    {
                        if (invoiceItems[i].Description == "ADT Annual Radio License Fee")
                        {
                            stringContent.Append(@"
                            {
                                'SelectionId': " + invoiceItems[i].ID + @",
                                'Quantity': 1,
                                'UnitPriceInclusive': " + invoiceItems[i].UnitPrice + @",
                                'TaxTypeId': " + taxTypeId + @"
                            },");
                        }
                    }
                }

                if (jobType == "Installation Invoice" || jobType == "Prorata Difference Invoice")
                {
                    for (int i = 0; i < invoiceItems.Count; i++)
                    {
                        if (invoiceItems[i].Description == "ADT Pro Rata")
                        {
                            stringContent.Append(@"
                            {
                                'SelectionId': " + invoiceItems[i].ID + @",
                                'Quantity': 1,
                                'UnitPriceInclusive': " + invoiceItems[i].UnitPrice + @",
                                'TaxTypeId': " + taxTypeId + @"
                            },");
                        }
                    }
                }

                if (jobType == "Service Invoice")
                {
                    for (int i = 0; i < invoiceItems.Count; i++)
                    {
                        if (invoiceItems[i].Description == "Services")
                        {
                            stringContent.Append(@"
                                    {
                                       'SelectionId': " + invoiceItems[i].ID + @",
                                       'Quantity': 1,
                                       'UnitPriceInclusive': " + invoiceItems[i].UnitPrice + @",
                                       'TaxTypeId': " + taxTypeIdVAT + @"
                                    }");
                        }
                    }
                }

                if (jobType == "Addon Invoice")
                {
                    for (int i = 0; i < invoiceItems.Count; i++)
                    {
                        if (invoiceItems[i].Description == "Add-ons")
                        {
                            stringContent.Append(@"
                                    {
                                       'SelectionId': " + invoiceItems[i].ID + @",
                                       'Quantity': 1,
                                       'UnitPriceInclusive': " + invoiceItems[i].UnitPrice + @",
                                       'TaxTypeId': " + taxTypeIdVAT + @"
                                    }");
                        }
                    }
                }

                stringContent.Append("]}");

                if (stringContent.ToString().Contains("SelectionId"))
                {
                    using (var httpClient = new HttpClient())
                    {
                        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                        HttpResponseMessage response = await httpClient.PostAsync($@"{targetURL}TaxInvoice/Save?useSystemDocumentNumber=true&apikey={apiKey}
                                                            &CompanyId={companyId}", new StringContent(stringContent.ToString(), Encoding.UTF8, "application/json"));
                        Log("TaxInvoice/Save", (int)response.StatusCode, response.ReasonPhrase, "");
                    }
                }
                else
                    Log("TaxInvoice/Save", -1, "", "Required invoice items not setup in Sage account");
            }
            catch (Exception ex)
            {
                Log("TaxInvoice/Save", -1, "", ex.Message);
            }
        }

        public DropDownList PopulateBankAccountsDropdown(DropDownList ddlBankAccounts)
        {
            SageAuthentication();

            if (!string.IsNullOrEmpty(targetURL) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(loginCredentials))
            {
                Task task = new Task(BankAccounts_MainMethod);
                task.Start();
                Thread.Sleep(3000);
                Functions fn = new Functions();
                fn.PopulateBankAccountsDropdownbox(ddlBankAccounts);
            }
            else
                Log("PopulateBankAccountsDropdown", -1, "", "Target URL and/or api key and/or username and/or password is missing");

            return ddlBankAccounts;
        }

        protected void BankAccounts_MainMethod()
        {
            requestType = "Get Bank Accounts";
            GetCompanyId();
        }

        protected async void GetBankAccounts()
        {
            try
            {
                string bankAccountsJson = string.Empty;

                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.GetAsync($@"{targetURL}BankAccount/Get?apikey={apiKey}&CompanyId={companyId}");
                    Log("BankAccount/Get", (int)response.StatusCode, response.ReasonPhrase, "");
                    bankAccountsJson = await response.Content.ReadAsStringAsync();
                }

                JObject bankAccountsObject = JObject.Parse(bankAccountsJson);
                IList<JToken> bankAccountsJsonJTokenList = bankAccountsObject["Results"].Children().ToList();
                IList<BankAccounts> bankAccountsObjectDotNetObjectList = new List<BankAccounts>();

                foreach (JToken result in bankAccountsJsonJTokenList)
                {
                    BankAccounts bankAccount = result.ToObject<BankAccounts>();
                    bankAccountsObjectDotNetObjectList.Add(bankAccount);
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("DELETE BankAccounts;");

                for (int i = 0; i < bankAccountsObjectDotNetObjectList.Count; i++)
                    sb.Append($@" INSERT INTO BankAccounts (BankAccountId, BankAccount) 
                                VALUES ({bankAccountsObjectDotNetObjectList[i].Id}, '{bankAccountsObjectDotNetObjectList[i].Name}'); ");

                if (sb.Length > 0)
                {
                    clsDataBase clsDb = new clsDataBase();
                    clsDb.NonQuery(sb.ToString());
                }
                else
                    Log("BankAccount/Get", -1, "", "No bank accounts setup in Sage");
            }
            catch (Exception ex)
            {
                Log("BankAccount/Get", -1, "", ex.Message);
            }
        }

        public void InsertReceipt()
        {
            SageAuthentication();

            if (!string.IsNullOrEmpty(targetURL) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(loginCredentials))
                GetCompanyId();
            else
                Log("", -1, "", "Target URL and/or api key and/or username and/or password is missing");
        }

        protected async void _InsertReceipt()
        {
            string api = string.Empty;

            try
            {
                string stringContent = string.Empty;

                switch (requestType)
                {
                    case "Insert Receipt":
                        api = "CustomerReceipt";
                        switch (paymentMethod)
                        {
                            case "Cash payment":
                                paymentMethodId = 1;
                                break;
                            case "Cheque payment":
                                paymentMethodId = 2;
                                break;
                            case "Bank card payment":
                                paymentMethodId = 3;
                                break;
                            case "EFT payment":
                            case "Debit order":
                            case "YOCO":
                            case "NETCASH":
                            case "PAYFAST":
                                paymentMethodId = 4;
                                break;
                        }
                        stringContent = @"{
                                  'CustomerId': " + sageCustomerId + @",
                                  'Date': '" + date + @"',
                                  'Total': " + receiptAmount + @",
                                  'BankAccountId': " + bankAccountId + @",
                                  'PaymentMethod': " + paymentMethodId + @",
                                  'Reference': '" + paymentId + @"'
                                }";
                        break;
                    case "Insert Credit Note":
                        api = "CustomerReturn";
                        int selectionId = 0;
                        for (int i = 0; i < invoiceItems.Count; i++)
                        {
                            if (invoiceItems[i].Description == "Sales")
                                selectionId = invoiceItems[i].ID;
                        }
                        stringContent = @"{
                                  'CustomerId': " + sageCustomerId + @",
                                  'Date': '" + DateTime.Now + @"',
                                  'Reference': '" + paymentId + @"',
                                  'Inclusive': true,
                                  'Lines': [
                                    { 
                                        'SelectionId': " + selectionId + @",
                                        'Quantity': 1, 
                                        'UnitPriceInclusive': " + receiptAmount + @",
                                        'Total': " + receiptAmount + @"
                                     }]
                                }";
                        break;
                    case "Insert Write Off":
                        api = "CustomerWriteOff";
                        stringContent = @"{
                                  'CustomerId': " + sageCustomerId + @",
                                  'Date': '" + DateTime.Now + @"',
                                  'Total': " + receiptAmount + @",
                                  'Reference': '" + paymentId + @"'
                                }";
                        break;
                }

                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.PostAsync($@"{targetURL}{api}/Save?useSystemDocumentNumber=true&apikey={apiKey}&CompanyId={companyId}",
                                                                                    new StringContent(stringContent, Encoding.UTF8, "application/json"));
                    Log(api + "/Save", (int)response.StatusCode, response.ReasonPhrase, "");

                    if ((int)response.StatusCode == 201)
                        _Allocate();
                }
            }
            catch (Exception ex)
            {
                Log(api + "/Save", -1, "", ex.Message);
            }
        }

        public async void _Allocate()
        {
            try
            {
                HttpResponseMessage response;
                clsDataBase clsDb = new clsDataBase();
                StringBuilder sb = new StringBuilder();
                string stringContent = string.Empty;
                bool paymentExists = true;
                string url = targetURL;
                string paymentType = clsDb.GetScalar($"select sTransactionType from ClientPaymentsAndCredit where pkiClientPaymentAndCreditId = {paymentId}");
                clsDb.CloseDBConnection();

                switch (paymentType)
                {
                    case "YOCO":
                    case "NETCASH":
                    case "PAYFAST":
                    case "Cash payment":
                    case "EFT payment":
                    case "Cheque payment":
                    case "Bank card payment":
                    case "Debit order":
                        url += "CustomerReceipt";
                        break;
                    case "Invoice Voided":
                    case "Credit awarded":
                        url += "CustomerReturn";
                        break;
                    case "Bad debt":
                        url += "CustomerWriteOff";
                        break;
                }

                url += $"/Get?apikey={apiKey}&CompanyId={companyId}&$filter=indexof(Reference, '{paymentId}') ge 0";

                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    response = await httpClient.GetAsync(url);
                    responseContent = await response.Content.ReadAsStringAsync();
                }

                JObject paymentObject = JObject.Parse(responseContent);
                IList<JToken> paymentJTokenList = paymentObject["Results"].Children().ToList();
                IList<SageId> paymentDotNetObjectList = new List<SageId>();

                foreach (JToken result in paymentJTokenList)
                {
                    SageId sageId = result.ToObject<SageId>();
                    paymentDotNetObjectList.Add(sageId);
                }

                int sagePaymentId = 0;

                if (paymentDotNetObjectList.Count > 0)
                    sagePaymentId = paymentDotNetObjectList[0].ID;

                if (responseContent == "null") paymentExists = false; else { }

                if (sagePaymentId != 0) { } else { paymentExists = false; }

                if (paymentExists)
                {
                    foreach (var allocation in allocationList)
                    {
                        url = targetURL;
                        url += $"TaxInvoice/Get?apikey={apiKey}&CompanyId={companyId}&$filter=indexof(Reference, '{allocation.InvoiceId}') ge 0";

                        using (var httpClient = new HttpClient())
                        {
                            response = await httpClient.GetAsync(url);
                            Log("TaxInvoice/Get", (int)response.StatusCode, response.ReasonPhrase, "");
                            responseContent = await response.Content.ReadAsStringAsync();
                        }

                        if (responseContent != "null" && response.ReasonPhrase == "OK") // status code 200 = OK
                        {
                            JObject invoiceObject = JObject.Parse(responseContent);
                            IList<JToken> invoiceJTokenList = invoiceObject["Results"].Children().ToList();
                            IList<SageId> invoiceDotNetObjectList = new List<SageId>();

                            foreach (JToken result in invoiceJTokenList)
                            {
                                SageId sageId = result.ToObject<SageId>();
                                invoiceDotNetObjectList.Add(sageId);
                            }

                            if (invoiceDotNetObjectList.Count > 0)
                            {
                                int sageInvoiceId = invoiceDotNetObjectList[0].ID;

                                url = targetURL;
                                url += $"Allocation/Save?apikey={apiKey}&CompanyId={companyId}";

                                stringContent = @"{
                                    'SourceDocumentId': " + sagePaymentId + @",
                                    'AllocatedToDocumentId': " + sageInvoiceId + @",
                                    'Total': " + allocation.Amount + @" 
                                }";

                                using (var httpClient = new HttpClient())
                                {
                                    response = await httpClient.PostAsync(url, new StringContent(stringContent, Encoding.UTF8, "application/json"));
                                    statusCode = (int)response.StatusCode;
                                    Log("Allocation/Save", (int)response.StatusCode, response.ReasonPhrase, "");
                                    responseContent = await response.Content.ReadAsStringAsync();
                                }

                                if (statusCode == 200)
                                {
                                    SageId id = JsonConvert.DeserializeObject<SageId>(responseContent);
                                    sb.Append($"UPDATE InvoicePayments SET AccountingSoftwareId = {id.ID} WHERE PaymentId = {allocation.PaymentId} AND InvoiceId = {allocation.InvoiceId}; ");
                                }
                            }
                            else
                                Log("Allocation/Save", -1, "", "The invoice can not be found in Sage hence the allocation could not be done in Sage");
                        }
                    }
                }
                else
                    Log("Allocation/Save", -1, "", "The payment cant be found in Sage hence the allocation could not be done in Sage");

                if (sb.Length > 0)
                    clsDb.NonQuery(sb.ToString());
            }
            catch (Exception ex)
            {
                Log("Allocation/Save", -1, "", ex.Message);
            }
        }

        public void DeleteReceipt()
        {
            SageAuthentication();

            if (!string.IsNullOrEmpty(targetURL) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(loginCredentials))
            {
                requestType = "Delete Receipt";
                GetCompanyId();
            }
            else
                Log("Delete Receipt", -1, "", "Target URL and/or api key and/or username and/or password is missing");
        }

        public async void GetReceiptId()
        {
            try
            {
                clsDataBase clsDb = new clsDataBase();
                string url = targetURL;
                bool paymentExists = true;
                string paymentType = clsDb.GetScalar($"select sTransactionType from ClientPaymentsAndCredit where pkiClientPaymentAndCreditId = {paymentId}");
                clsDb.CloseDBConnection();

                switch (paymentType)
                {
                    case "YOCO":
                    case "NETCASH":
                    case "PAYFAST":
                    case "Cash payment":
                    case "EFT payment":
                    case "Cheque payment":
                    case "Bank card payment":
                    case "Debit order":
                        url += "CustomerReceipt";
                        break;
                    case "Credit awarded":
                        url += "CustomerReturn";
                        break;
                    case "Bad debt":
                        url += "CustomerWriteOff";
                        break;
                }

                url += $"/Get?apikey={apiKey}&CompanyId={companyId}&$filter=indexof(Reference, '{paymentId}') ge 0";

                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.GetAsync(url);
                    responseContent = await response.Content.ReadAsStringAsync();
                }

                JObject paymentObject = JObject.Parse(responseContent);
                IList<JToken> paymentJTokenList = paymentObject["Results"].Children().ToList();
                IList<SageId> paymentDotNetObjectList = new List<SageId>();
                foreach (JToken result in paymentJTokenList)
                {
                    SageId sageId = result.ToObject<SageId>();
                    paymentDotNetObjectList.Add(sageId);
                }

                int sagePaymentId = 0;

                if (paymentDotNetObjectList.Count > 0)
                {
                    sagePaymentId = paymentDotNetObjectList[0].ID;
                    receiptId = paymentDotNetObjectList[0].ID;
                }

                if (responseContent == "null") paymentExists = false; else { }

                if (sagePaymentId != 0) { } else { paymentExists = false; }

                if (paymentExists)
                {
                    if (allocationId != 0)
                        _DeleteAllocation();
                    else
                        _DeleteReceipt();
                }
                else
                {
                    string message;

                    if (allocationId != 0)
                        message = "The allocation could not be deleted because the Sage payment ID is missing";
                    else
                        message = "The payment could not be deleted because the payment could not be found in Sage";

                    Log("GetReceiptId", -1, "", message);
                }
            }
            catch (Exception ex)
            {
                Log("GetReceiptId", -1, "", ex.Message);
            }
        }

        public void DeleteAllocation()
        {
            SageAuthentication();

            if (!string.IsNullOrEmpty(targetURL) && !string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(loginCredentials))
                GetCompanyId();
            else
                Log("DeleteAllocation", -1, "", "Target URL and / or api key and / or username and / or password is missing");
        }

        public async void _DeleteAllocation()
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.DeleteAsync($@"{targetURL}Allocation/Delete?Id={allocationId}&apikey={apiKey}&CompanyId={companyId}");
                    Log("Allocation/Delete", (int)response.StatusCode, response.ReasonPhrase, "");
                }

                if (requestType == "Delete Receipt")
                    _DeleteReceipt();
            }
            catch (Exception ex)
            {
                Log("Allocation/Delete", -1, "", ex.Message);
            }
        }

        public async void _DeleteReceipt()
        {
            try
            {
                using (var httpClient = new HttpClient())
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpResponseMessage response = await httpClient.PostAsync($"{targetURL}CustomerReceipt/Delete?Id={receiptId}&apikey={apiKey}&CompanyId={companyId}",
                                                                                new StringContent("", Encoding.UTF8, "application/json"));
                    Log("CustomerReceipt/Delete", (int)response.StatusCode, response.ReasonPhrase, "");
                }
            }
            catch (Exception ex)
            {
                Log("CustomerReceipt/Delete", -1, "", ex.Message);
            }
        }

    }

    public class Company
    {
        public int Id { get; set; }
    }

    public class SageId
    {
        public int ID { get; set; }
    }

    public class TaxType
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class Item
    {
        public int ID { get; set; }
        public string Description { get; set; }
        public double UnitPrice { get; set; }
    }

    public class BankAccounts
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}