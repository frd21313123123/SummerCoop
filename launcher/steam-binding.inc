// Lossless editing of the My Summer Car launch option in Steam's KeyValues.
// All offsets refer to the original UTF-8 document; other settings stay intact.
namespace steam_binding {
struct Node {
    std::string key, value;
    size_t begin = 0, end = 0, close = 0;
    bool object = false;
    std::vector<Node> children;
};

class Parser {
    const std::string &text;
    size_t pos = 0;
    void Space() {
        for (;;) {
            while (pos < text.size() && (unsigned char)text[pos] <= ' ') ++pos;
            if (text.compare(pos, 2, "//")) break;
            pos = text.find('\n', pos + 2);
            if (pos == std::string::npos) { pos = text.size(); break; }
        }
    }
    bool Token(std::string &out, size_t &begin, size_t &end) {
        Space(); begin = pos;
        if (pos == text.size() || text[pos] == '{' || text[pos] == '}') return false;
        if (text[pos] != '"') {
            while (pos < text.size() && (unsigned char)text[pos] > ' ' && text[pos] != '{' && text[pos] != '}') out += text[pos++];
            end = pos; return !out.empty();
        }
        ++pos;
        while (pos < text.size()) {
            char c = text[pos++];
            if (c == '"') { end = pos; return true; }
            if (c == '\\' && pos < text.size()) {
                char next = text[pos];
                if (next == '\\' || next == '"') { c = next; ++pos; }
                else if (next == 'n' || next == 'r' || next == 't') { c = next == 'n' ? '\n' : next == 'r' ? '\r' : '\t'; ++pos; }
            }
            out += c;
        }
        return false;
    }
    bool List(std::vector<Node> &nodes, unsigned depth, size_t &close) {
        if (depth > 32) return false;
        for (;;) {
            Space();
            if (pos == text.size()) return depth == 0;
            if (text[pos] == '}') { close = pos++; return depth != 0; }
            Node n;
            size_t unused = 0;
            if (!Token(n.key, unused, unused)) return false;
            Space();
            if (pos < text.size() && text[pos] == '{') {
                n.object = true; ++pos;
                if (!List(n.children, depth + 1, n.close)) return false;
            } else if (!Token(n.value, n.begin, n.end)) return false;
            nodes.push_back(n);
        }
    }
public:
    explicit Parser(const std::string &value) : text(value) {}
    bool Read(std::vector<Node> &nodes) {
        if (text.size() > 16u << 20) return false;
        if (text.compare(0, 3, "\xef\xbb\xbf") == 0) pos = 3;
        size_t close = 0;
        return List(nodes, 0, close);
    }
};

static const Node *Child(const std::vector<Node> &nodes, const char *key) {
    const Node *found = NULL;
    for (const Node &n : nodes) if (!_stricmp(n.key.c_str(), key)) {
        if (found) return NULL; // Do not guess when Steam's document is ambiguous.
        found = &n;
    }
    return found;
}
static std::string Quote(const std::string &value) {
    std::string result = "\"";
    for (char c : value) {
        if (c == '\\' || c == '"') result += '\\';
        if (c == '\n') result += "\\n";
        else if (c == '\r') result += "\\r";
        else if (c == '\t') result += "\\t";
        else result += c;
    }
    return result + '"';
}
static bool LauncherName(const std::wstring &path) {
    size_t slash = path.find_last_of(L"\\/");
    std::wstring name = path.substr(slash == std::wstring::npos ? 0 : slash + 1);
    for (wchar_t &c : name) c = (wchar_t)towlower(c);
    if (name == L"summercoop-launcher.exe" || name == L"summercoop.exe" || name == L"mwcoop-launcher.exe" || name == L"mwcoop.exe") return true;
    const std::wstring prefix = L"summercoop-launcher-";
    if (name.compare(0, prefix.size(), prefix) || name.size() <= prefix.size() + 4 || name.substr(name.size() - 4) != L".exe") return false;
    std::wstring version = name.substr(prefix.size(), name.size() - prefix.size() - 4);
    return version[0] >= L'0' && version[0] <= L'9' && version.find_first_not_of(L"0123456789abcdefghijklmnopqrstuvwxyz.-+") == std::wstring::npos;
}
static std::wstring FullPath(std::wstring path) {
    for (wchar_t &c : path) if (c == L'/') c = L'\\';
    if (path.size() < 3 || !iswalpha(path[0]) || path[1] != L':' || path[2] != L'\\') return L"";
    wchar_t full[32768];
    DWORD size = GetFullPathNameW(path.c_str(), _countof(full), full, NULL);
    return size && size < _countof(full) ? std::wstring(full) : L"";
}
static bool SamePath(const std::wstring &a, const std::wstring &b) {
    return !_wcsicmp(a.c_str(), b.c_str());
}
static std::wstring FromUtf8(const std::string &text) {
    int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), (int)text.size(), NULL, 0);
    if (!count) return L"";
    std::wstring result(count, 0);
    MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), (int)text.size(), &result[0], count);
    return result;
}
static std::string Utf8(const std::wstring &text) {
    int count = WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), NULL, 0, NULL, NULL);
    if (!count) return "";
    std::string result(count, 0);
    WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), &result[0], count, NULL, NULL);
    return result;
}

// Return false for another wrapper or a relative/ambiguous command. Retain all
// game arguments, including %command% and accepted Steam invitations.
static bool LaunchLine(const std::string &oldLine, const std::wstring &launcher, std::string &line, std::wstring &oldPath) {
    std::wstring value = FromUtf8(oldLine);
    if (!oldLine.empty() && value.empty()) return false;
    size_t start = value.find_first_not_of(L" \t\r\n");
    if (start == std::wstring::npos) { line = Utf8(L"\"" + launcher + L"\" %command%"); return true; }
    size_t end = start, pathStart = start;
    if (value[start] == L'"') {
        pathStart = start + 1; end = value.find(L'"', pathStart);
        if (end == std::wstring::npos) return false;
        oldPath = FullPath(value.substr(pathStart, end - pathStart)); ++end;
        if (end < value.size() && value[end] != L' ' && value[end] != L'\t') return false;
    } else {
        end = value.find_first_of(L" \t\r\n", start);
        if (end == std::wstring::npos) end = value.size();
        oldPath = FullPath(value.substr(start, end - start));
    }
    if (oldPath.empty() || !LauncherName(oldPath)) { oldPath.clear(); return false; }
    std::wstring suffix = value.substr(end);
    if (suffix.find(L"%command%") == std::wstring::npos) suffix += L" %command%";
    line = Utf8(value.substr(0, start) + L"\"" + launcher + L"\"" + suffix);
    return true;
}

struct Patch {
    bool valid = false, supported = false;
    std::string text;
    std::wstring oldPath;
};
static Patch Rewrite(const std::string &text, const std::wstring &launcher) {
    Patch result;
    std::vector<Node> nodes;
    if (!Parser(text).Read(nodes) || launcher.empty()) return result;
    result.valid = true;
    const Node *node = NULL;
    const std::vector<Node> *children = &nodes;
    for (const char *key : { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "516750" }) {
        node = Child(*children, key);
        if (!node || !node->object) return result;
        children = &node->children;
    }
    const Node *option = Child(*children, "LaunchOptions");
    // A duplicate key is unsafe even though Child also returns NULL for absence.
    unsigned matches = 0;
    for (const Node &n : *children) if (!_stricmp(n.key.c_str(), "LaunchOptions")) ++matches;
    if (matches > 1 || (option && option->object)) return result;
    std::string line;
    if (!LaunchLine(option ? option->value : "", launcher, line, result.oldPath)) return result;
    result.supported = true;
    result.text = text;
    if (option) result.text.replace(option->begin, option->end - option->begin, Quote(line));
    else {
        size_t lineStart = text.rfind('\n', node->close);
        lineStart = lineStart == std::string::npos ? 0 : lineStart + 1;
        std::string indent = text.substr(lineStart, node->close - lineStart);
        if (indent.find_first_not_of(" \t") != std::string::npos) indent.clear();
        std::string newline = text.find("\r\n") != std::string::npos ? "\r\n" : "\n";
        // Insert before the closing line, without moving its existing indentation.
        if (indent.empty() && lineStart != node->close) {
            result.text.insert(node->close, newline + "\t\"LaunchOptions\"\t\t" + Quote(line) + newline);
        } else {
            result.text.insert(lineStart, indent + "\t\"LaunchOptions\"\t\t" + Quote(line) + newline);
        }
    }
    return result;
}
} // namespace steam_binding
