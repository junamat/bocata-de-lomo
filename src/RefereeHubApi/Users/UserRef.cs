using System.Globalization;

namespace RefereeHubApi.Users
{
    // a user given either by id or by username. room actions take this so callers can use either.
    public readonly record struct UserRef
    {
        public int? Id { get; }

        public string? Username { get; }

        private UserRef(int? id, string? username)
        {
            Id = id;
            Username = username;
        }

        public static UserRef FromId(int id) => new UserRef(id, null);

        public static UserRef FromName(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username is empty.", nameof(username));

            return new UserRef(null, username.Trim());
        }

        // "#123" or "123" is an id, "@name" is always a name (for names made only of digits), anything else is a name.
        public static UserRef Parse(string text)
        {
            text = text.Trim();

            if (text.StartsWith('@'))
                return FromName(text[1..]);

            string digits = text.StartsWith('#') ? text[1..] : text;

            if (digits.Length > 0 && digits.All(char.IsAsciiDigit) && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                return FromId(id);

            if (text.StartsWith('#'))
                throw new FormatException($"'{text}' is not a valid user id.");

            return FromName(text);
        }

        public static implicit operator UserRef(int id) => FromId(id);

        public override string ToString() => Id is int id ? $"#{id}" : Username ?? string.Empty;
    }

    public class UserNotFoundException : Exception
    {
        public string Username { get; }

        public UserNotFoundException(string username)
            : base($"No osu! user named '{username}'.")
        {
            Username = username;
        }
    }
}
