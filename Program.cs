using DbMapping;
using Neo4j.Driver;

string dbUri = "bolt://localhost:7687";
IDriver driver = GraphDatabase.Driver(dbUri);

IAsyncSession session = driver.AsyncSession();
IResultCursor cursor = await session.RunAsync("RETURN {greetings: [{message: \"Hello world!\", recipient: \"Joshua\" }, {message: \"Hello C#!\", recipient: \"Xiaoli\" }], date: date()} AS result;");
Envelope envelope = await cursor.SingleAsync<Envelope>();

class Greeting
{
    public string Message { get; set; } = string.Empty;
    public string Recipient { get; set; } = string.Empty;
}

class Envelope
{
    public List<Greeting> Greetings { get; set; } = [];
    public DateOnly Date { get; set; }
}