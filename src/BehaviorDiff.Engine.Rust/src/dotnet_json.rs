use serde::Serialize;

pub(crate) fn to_vec_pretty<T: Serialize>(value: &T) -> Result<Vec<u8>, String> {
    let source = serde_json::to_string_pretty(value).map_err(|error| error.to_string())?;
    let encoded = encode_strings(&source)?;
    #[cfg(windows)]
    let encoded = encoded.replace('\n', "\r\n");
    Ok(encoded.into_bytes())
}

pub(crate) fn to_vec<T: Serialize>(value: &T) -> Result<Vec<u8>, String> {
    let source = serde_json::to_string(value).map_err(|error| error.to_string())?;
    Ok(encode_strings(&source)?.into_bytes())
}

fn encode_strings(source: &str) -> Result<String, String> {
    let mut input = source.chars().peekable();
    let mut output = String::with_capacity(source.len());
    while let Some(character) = input.next() {
        output.push(character);
        if character != '"' {
            continue;
        }
        loop {
            let character = input
                .next()
                .ok_or_else(|| "unterminated JSON string".to_owned())?;
            if character == '"' {
                output.push('"');
                break;
            }
            let decoded = if character == '\\' {
                decode_escape(&mut input)?
            } else {
                character
            };
            encode_character(decoded, &mut output);
        }
    }
    Ok(output)
}

fn decode_escape<I: Iterator<Item = char>>(
    input: &mut std::iter::Peekable<I>,
) -> Result<char, String> {
    let escape = input
        .next()
        .ok_or_else(|| "unterminated JSON escape".to_owned())?;
    Ok(match escape {
        '"' => '"',
        '\\' => '\\',
        '/' => '/',
        'b' => '\u{0008}',
        'f' => '\u{000c}',
        'n' => '\n',
        'r' => '\r',
        't' => '\t',
        'u' => {
            let first = read_hex_quad(input)?;
            if (0xd800..=0xdbff).contains(&first) {
                if input.next() != Some('\\') || input.next() != Some('u') {
                    return Err("invalid JSON surrogate pair".to_owned());
                }
                let second = read_hex_quad(input)?;
                if !(0xdc00..=0xdfff).contains(&second) {
                    return Err("invalid JSON low surrogate".to_owned());
                }
                let scalar = 0x10000 + (((first - 0xd800) as u32) << 10) + (second - 0xdc00) as u32;
                char::from_u32(scalar).ok_or_else(|| "invalid JSON scalar".to_owned())?
            } else {
                char::from_u32(first as u32).ok_or_else(|| "invalid JSON scalar".to_owned())?
            }
        }
        _ => return Err(format!("unsupported JSON escape: \\{escape}")),
    })
}

fn read_hex_quad<I: Iterator<Item = char>>(
    input: &mut std::iter::Peekable<I>,
) -> Result<u16, String> {
    let mut value = 0_u16;
    for _ in 0..4 {
        let digit = input
            .next()
            .and_then(|character| character.to_digit(16))
            .ok_or_else(|| "invalid JSON unicode escape".to_owned())?;
        value = (value << 4) | digit as u16;
    }
    Ok(value)
}

fn encode_character(character: char, output: &mut String) {
    match character {
        '"' => output.push_str("\\u0022"),
        '&' => output.push_str("\\u0026"),
        '\'' => output.push_str("\\u0027"),
        '+' => output.push_str("\\u002B"),
        '<' => output.push_str("\\u003C"),
        '>' => output.push_str("\\u003E"),
        '\\' => output.push_str("\\\\"),
        '`' => output.push_str("\\u0060"),
        '\u{0008}' => output.push_str("\\b"),
        '\u{0009}' => output.push_str("\\t"),
        '\u{000a}' => output.push_str("\\n"),
        '\u{000c}' => output.push_str("\\f"),
        '\u{000d}' => output.push_str("\\r"),
        value if value <= '\u{001f}' => output.push_str(&format!("\\u{:04X}", value as u32)),
        value if value.is_ascii() => output.push(value),
        value if (value as u32) <= 0xffff => {
            output.push_str(&format!("\\u{:04X}", value as u32));
        }
        value => {
            let scalar = value as u32 - 0x10000;
            let high = 0xd800 + (scalar >> 10);
            let low = 0xdc00 + (scalar & 0x3ff);
            output.push_str(&format!("\\u{high:04X}\\u{low:04X}"));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_system_text_json_default_string_escaping() {
        let source = serde_json::to_string(&"\"&'+<>\\`漢🚀\n").unwrap();
        let encoded = encode_strings(&source).unwrap();
        assert_eq!(
            encoded,
            "\"\\u0022\\u0026\\u0027\\u002B\\u003C\\u003E\\\\\\u0060\\u6F22\\uD83D\\uDE80\\n\""
        );
    }
}
