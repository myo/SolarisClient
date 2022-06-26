using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;


namespace SolarisClient
{
    internal class Discipline
    {
        internal Discipline(string _name, int _credits, int _grade = 0)
        {
            name = _name;
            credits = _credits;
            grade = _grade;
        }
        internal string name { get; private set; }
        internal int credits { get; private set; }
        internal int grade { get; set; }
    }

    internal class Program
    {
        //made it a dictionary expecting to later modify it
        //we're using a second dictionary to store modifications so
        //this doesn't have to be a dictionary anymore
        static Dictionary<int, Discipline> fetchedDisciplineData = new Dictionary<int, Discipline>();

        static void Main(string[] args)
        {
            //let the user input the page of the "Solaris" system that doesn't work
            Console.Write("Enter Solaris URL: ");
            string solarisUrlInitial = Console.ReadLine();

            //the input can look in a number of different ways, let's extract what we need.
            MatchCollection solarisUrlMatches = Regex.Matches(solarisUrlInitial, @"(https?)|([a-zA-Z0-9\-\.]+)");

            //the actual url we're going to use.
            string solarisUrl = solarisUrlMatches.Count > 1 ? solarisUrlMatches[1].Groups[0].Value : solarisUrlMatches[0].Groups[0].Value;

            Console.Title = "SolarisClient for " + solarisUrl;

            //initialize a HttpClient
            HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
            //you might need to change this one?
            //"en", "ro", "fr", "de", has no effect whatsoever on our app
            client.DefaultRequestHeaders.Add("lang", "ro");
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:101.0) Gecko/20100101 Firefox/101.0");

            //should we pretend we're a real browser, or use our own User-Agent and skip this?
            client.DefaultRequestHeaders.Referrer = new Uri("https://" + solarisUrl + "/login");

            //everything below could be split to different function that takes client as param
            HttpResponseMessage cookieResponse = client.GetAsync("https://" + solarisUrl + "/backend/commons/user").Result;
            List<string> cookieResponseHeaders = new List<string>();

            //we try to get the cookie param by which server identifies us
            if (cookieResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string> tempCookieResponseHeaders))
            {
                cookieResponseHeaders = tempCookieResponseHeaders.ToList();
            }
            else
            {
                Console.WriteLine("Error: Couldn't find `Set-Cookie` header.");
                return;
            }

            //there's only one Set-Cookie header
            client.DefaultRequestHeaders.Add("Cookie", cookieResponseHeaders.FirstOrDefault());

            //auth, we loop this until it succeeds.
            login:
            Console.Write("Username: ");
            string username = Console.ReadLine();
            //passwords are by default contained in the username
            //so there's no need to hide them.
            Console.Write("Password: ");
            string password = Console.ReadLine();

            //this might look like bad code but it's fast code.
            HttpContent postData = new StringContent("{\"username\":\"" + username + "\",\"password\":\"" + password + "\"}");
            postData.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            HttpResponseMessage userDataResponse = client.PostAsync("https://" + solarisUrl + "/backend/commons/user/login", postData).Result;

            //username and password no longer need to be displayed
            Console.Clear();
            if (userDataResponse.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                Console.WriteLine("Error: Invalid username or password, try again.");
                goto login;
            }

            //the serialized post-login user data json
            string userDataJson = userDataResponse.Content.ReadAsStringAsync().Result;
            //deserializing it, we *don't* need 2 variables but it looks cleaner this way..
            //we don't deserialize by providing a type because I want to avoid copyright issues.
            JsonDocument userDataDocument = JsonDocument.Parse(userDataJson);
            JsonElement userData = userDataDocument.RootElement;

            //see if it's valid user data we received.
            if (userData.TryGetProperty("_firstName", out JsonElement firstName))
            {
                Console.WriteLine("Hello, " + firstName.GetString() + "!");
            }
            else
            {
                Console.WriteLine("Error: Couldn't find `_firstName`.");
                return;
            }

            //we might use this later to provide more statistics
            JsonElement.ArrayEnumerator userYears = userData.GetProperty("_students").EnumerateArray().FirstOrDefault()
                                                            .GetProperty("_contexts").EnumerateArray();
            //an array with all the disciplines, sadly it doesn't contain grades for them
            JsonElement.ArrayEnumerator currentYearDisciplines = userYears.LastOrDefault()
                                                                          .GetProperty("_studentYearDisciplines").EnumerateArray();

            //so we have to loop the array and get the grades for each discipline
            foreach(JsonElement discipline in currentYearDisciplines)
            {
                //the only param we need from the data in this array
                int id = discipline.GetProperty("_id").GetInt32();

                //a real browser would change refferer on each discipline..
                client.DefaultRequestHeaders.Referrer = new Uri("https://" + solarisUrl + "/portal/information/scholar-situation/" + id);
                
                //request the discipline data
                HttpResponseMessage disciplineDataResponse = client.GetAsync("https://" + solarisUrl + "/backend/portal/information/scholarSituation/" + id).Result;
                
                //serialized discipline json
                string disciplineDataJson = disciplineDataResponse.Content.ReadAsStringAsync().Result;
                
                //deserialize it
                JsonDocument disciplineDataDocument = JsonDocument.Parse(disciplineDataJson);
                JsonElement disciplineData = disciplineDataDocument.RootElement;

                //properties we're going to need
                string name = disciplineData.GetProperty("_name").GetString();
                int credits = disciplineData.GetProperty("_number_of_credits").GetInt32();
                int final_grade = 0;

                //sometimes _final_grade is set, sometimes only _situation (see below) is set.
                if (disciplineData.TryGetProperty("_final_grade", out JsonElement finalGradeElement))
                {
                    int grade = finalGradeElement.GetInt32();
                    if (grade > 0)
                    {
                        final_grade = grade;
                    }
                }
                if (final_grade == 0)
                {
                    //_final_grade isn't set, try to see if there are examinations
                    if (disciplineData.TryGetProperty("examinations_information", out JsonElement examinationsElement))
                    {
                        //there are examinations, let's read them as an array
                        JsonElement.ArrayEnumerator examinations = examinationsElement.EnumerateArray();
                        //could it be an empty array? better check!
                        if (examinations.Any())
                        {
                            //check the last grade
                            string situation = examinations.LastOrDefault().GetProperty("_situation").GetString();
                            //sometimes it holds a value of "undefined_situation", we set it to 0 then.
                            final_grade = situation == "undefined_situation" ? 0 : Int32.Parse(situation);
                        }
                    }
                }
                //save the discipline to our dict
                fetchedDisciplineData.Add(id, new Discipline(name, credits, final_grade));
            }

            //order disciplines by grades
            fetchedDisciplineData = fetchedDisciplineData.OrderByDescending(d => d.Value.grade).ToDictionary(d=>d.Key, d=>d.Value);

            //we use this because you shouldn't modify a dictionary while looping through its
            //elements, which we're later doing.
            Dictionary<int, Discipline> adjustedDisciplineData = new Dictionary<int, Discipline>();

            Console.WriteLine("Your grades are:");
            foreach(KeyValuePair<int, Discipline> discipline in fetchedDisciplineData)
            {
                //if passed, show the grade
                if (discipline.Value.grade > 4)
                {
                    adjustedDisciplineData.Add(discipline.Key, discipline.Value);
                    Console.WriteLine(discipline.Value.name + " - " + discipline.Value.grade);
                }
                //if not passed or no grade, ask for what the student expects
                else
                {
                    if (discipline.Value.grade > 0)
                        Console.WriteLine(discipline.Value.name + " - " + discipline.Value.grade);
                    get_expected:
                    Console.Write("Type expected grade for " + discipline.Value.name + ": ");
                    int expected_grade = 0;
                    if (!Int32.TryParse(Console.ReadLine(), out expected_grade))
                    {
                        goto get_expected;
                    }
                    adjustedDisciplineData.Add(discipline.Key, new Discipline(discipline.Value.name, discipline.Value.credits, expected_grade));
                }
            }

            //maximum credits that can be earned in that year
            int maximumCredits = adjustedDisciplineData.Sum(d => d.Value.credits * 10);
            //credits that the student earned
            int creditsGained = adjustedDisciplineData.Sum(d => d.Value.grade * d.Value.credits);
            //the resulting year grade
            float yearGrade = (float)creditsGained / (float)maximumCredits * 10;

            Console.WriteLine("Expected Year Grade: " + yearGrade);

            //#TODO: should we goto back to where we ask student for expected grades?
            //so that he tries with different ones?
            Console.ReadLine();
        }
    }
}
