use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet, VecDeque};
use std::hash::{BuildHasher, Hash};
use std::rc::{Rc, Weak as RcWeak};
use std::sync::{Arc, Weak as ArcWeak};
use std::time::{Instant, SystemTime};

const MAX_DEPTH: usize = 8;
const RENDERED_CAP: usize = 4096;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CapturedValue {
    pub digest: String,
    pub rendered: String,
    pub partial: bool,
    pub values_digested: u64,
    pub depth_limited: u64,
    pub blocklisted: u64,
    pub rendered_truncated: u64,
    canonical: String,
}

#[derive(Default)]
pub struct CanonicalContext {
    depth: usize,
    next_reference: u64,
    references: HashMap<usize, u64>,
    values_digested: u64,
    depth_limited: u64,
    blocklisted: u64,
    partial: bool,
}

pub trait Canonicalize {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String);
}

pub fn capture<T: Canonicalize + ?Sized>(value: &T) -> CapturedValue {
    let mut context = CanonicalContext::default();
    let mut canonical = String::new();
    value.write_canonical(&mut context, &mut canonical);
    finish_capture(canonical, context)
}

pub fn capture_skipped(detail: &str) -> CapturedValue {
    let mut context = CanonicalContext::default();
    let mut canonical = String::new();
    context.write_skipped(&mut canonical, detail);
    finish_capture(canonical, context)
}

pub fn capture_arguments(values: Vec<(&'static str, CapturedValue)>) -> Option<CapturedValue> {
    if values.is_empty() {
        return None;
    }
    let mut context = CanonicalContext::default();
    let mut canonical = String::from("args{");
    for (name, value) in values {
        write_text(&mut canonical, name);
        canonical.push('=');
        canonical.push_str(&value.canonical);
        canonical.push(';');
        context.values_digested += value.values_digested;
        context.depth_limited += value.depth_limited;
        context.blocklisted += value.blocklisted;
        context.partial |= value.partial;
    }
    canonical.push('}');
    finish_capture(canonical, context).into()
}

fn finish_capture(canonical: String, context: CanonicalContext) -> CapturedValue {
    let digest = format!(
        "sha256:{}",
        hex::encode(Sha256::digest(canonical.as_bytes()))
    );
    let (rendered, rendered_truncated) = if canonical.len() > RENDERED_CAP {
        let mut end = RENDERED_CAP;
        while !canonical.is_char_boundary(end) {
            end -= 1;
        }
        (format!("{}<truncated>", &canonical[..end]), 1)
    } else {
        (canonical.clone(), 0)
    };
    CapturedValue {
        digest,
        rendered,
        partial: context.partial || rendered_truncated != 0,
        values_digested: context.values_digested,
        depth_limited: context.depth_limited,
        blocklisted: context.blocklisted,
        rendered_truncated,
        canonical,
    }
}

pub fn write_field<T: Canonicalize + ?Sized>(
    context: &mut CanonicalContext,
    output: &mut String,
    name: &str,
    value: &T,
) {
    write_text(output, name);
    output.push('=');
    value.write_canonical(context, output);
    output.push(';');
}

impl CanonicalContext {
    pub fn write_value(
        &mut self,
        type_name: &str,
        output: &mut String,
        write: impl FnOnce(&mut Self, &mut String),
    ) {
        self.values_digested += 1;
        if self.depth >= MAX_DEPTH {
            self.depth_limited += 1;
            self.partial = true;
            output.push_str("<depth:");
            write_text(output, type_name);
            output.push('>');
            return;
        }
        self.depth += 1;
        write(self, output);
        self.depth -= 1;
    }

    pub fn write_reference(
        &mut self,
        address: usize,
        type_name: &str,
        output: &mut String,
        write: impl FnOnce(&mut Self, &mut String),
    ) {
        if let Some(reference) = self.references.get(&address) {
            output.push_str("ref:");
            output.push_str(&reference.to_string());
            return;
        }
        let reference = self.next_reference;
        self.next_reference += 1;
        self.references.insert(address, reference);
        output.push_str("object:");
        output.push_str(&reference.to_string());
        output.push(':');
        write_text(output, type_name);
        output.push('{');
        write(self, output);
        output.push('}');
    }

    pub fn write_skipped(&mut self, output: &mut String, detail: &str) {
        self.values_digested += 1;
        self.blocklisted += 1;
        self.partial = true;
        output.push_str("<skipped:");
        write_text(output, detail);
        output.push('>');
    }
}

macro_rules! scalar {
    ($($type:ty => $tag:literal),+ $(,)?) => {
        $(impl Canonicalize for $type {
            fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
                context.write_value($tag, output, |_, output| {
                    output.push_str($tag);
                    output.push(':');
                    output.push_str(&self.to_string());
                });
            }
        })+
    };
}

scalar!(
    i8 => "i8", i16 => "i16", i32 => "i32", i64 => "i64", i128 => "i128", isize => "isize",
    u8 => "u8", u16 => "u16", u32 => "u32", u64 => "u64", u128 => "u128", usize => "usize"
);

impl Canonicalize for bool {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("bool", output, |_, output| {
            output.push_str(if *self { "bool:true" } else { "bool:false" });
        });
    }
}

impl Canonicalize for char {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("char", output, |_, output| {
            output.push_str("char:");
            write_text(output, &self.to_string());
        });
    }
}

impl Canonicalize for f32 {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("f32", output, |_, output| {
            write_float(*self as f64, "f32", output)
        });
    }
}

impl Canonicalize for f64 {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("f64", output, |_, output| write_float(*self, "f64", output));
    }
}

impl Canonicalize for str {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("str", output, |_, output| {
            output.push_str("str:");
            write_text(output, self);
        });
    }
}

impl Canonicalize for String {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        self.as_str().write_canonical(context, output);
    }
}

impl Canonicalize for () {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("unit", output, |_, output| output.push_str("unit"));
    }
}

impl<T: Canonicalize + ?Sized> Canonicalize for &T {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        let address = std::ptr::from_ref(*self).cast::<()>() as usize;
        context.write_reference(
            address,
            std::any::type_name::<T>(),
            output,
            |context, output| {
                (*self).write_canonical(context, output);
            },
        );
    }
}

impl<T: Canonicalize + ?Sized> Canonicalize for Box<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        let address = std::ptr::from_ref(self.as_ref()).cast::<()>() as usize;
        context.write_reference(
            address,
            std::any::type_name::<T>(),
            output,
            |context, output| {
                self.as_ref().write_canonical(context, output);
            },
        );
    }
}

impl<T: Canonicalize + ?Sized> Canonicalize for Rc<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        let address = Rc::as_ptr(self).cast::<()>() as usize;
        context.write_reference(
            address,
            std::any::type_name::<T>(),
            output,
            |context, output| {
                self.as_ref().write_canonical(context, output);
            },
        );
    }
}

impl<T: Canonicalize + ?Sized> Canonicalize for Arc<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        let address = Arc::as_ptr(self).cast::<()>() as usize;
        context.write_reference(
            address,
            std::any::type_name::<T>(),
            output,
            |context, output| {
                self.as_ref().write_canonical(context, output);
            },
        );
    }
}

impl<T: Canonicalize + ?Sized> Canonicalize for RcWeak<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        match self.upgrade() {
            Some(value) => value.write_canonical(context, output),
            None => context.write_value("Weak", output, |_, output| output.push_str("Weak:dropped")),
        }
    }
}

impl<T: Canonicalize + ?Sized> Canonicalize for ArcWeak<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        match self.upgrade() {
            Some(value) => value.write_canonical(context, output),
            None => context.write_value("Weak", output, |_, output| output.push_str("Weak:dropped")),
        }
    }
}

impl Canonicalize for SystemTime {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("SystemTime", output, |_, output| output.push_str("SystemTime:normalized"));
    }
}

impl Canonicalize for Instant {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("Instant", output, |_, output| output.push_str("Instant:normalized"));
    }
}

impl<T: Canonicalize> Canonicalize for Option<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("Option", output, |context, output| match self {
            Some(value) => {
                output.push_str("Option:Some(");
                value.write_canonical(context, output);
                output.push(')');
            }
            None => output.push_str("Option:None"),
        });
    }
}

impl<T: Canonicalize, E: Canonicalize> Canonicalize for Result<T, E> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        context.write_value("Result", output, |context, output| match self {
            Ok(value) => {
                output.push_str("Result:Ok(");
                value.write_canonical(context, output);
                output.push(')');
            }
            Err(error) => {
                output.push_str("Result:Err(");
                error.write_canonical(context, output);
                output.push(')');
            }
        });
    }
}

impl<T: Canonicalize, const N: usize> Canonicalize for [T; N] {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_sequence("array", self.iter(), context, output);
    }
}

impl<T: Canonicalize> Canonicalize for [T] {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_sequence("slice", self.iter(), context, output);
    }
}

impl<T: Canonicalize> Canonicalize for Vec<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_sequence("Vec", self.iter(), context, output);
    }
}

impl<T: Canonicalize> Canonicalize for VecDeque<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_sequence("VecDeque", self.iter(), context, output);
    }
}

impl<K: Canonicalize, V: Canonicalize> Canonicalize for BTreeMap<K, V> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_map("BTreeMap", self.iter(), false, context, output);
    }
}

impl<T: Canonicalize> Canonicalize for BTreeSet<T> {
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_set("BTreeSet", self.iter(), false, context, output);
    }
}

impl<K, V, S> Canonicalize for HashMap<K, V, S>
where
    K: Canonicalize + Eq + Hash,
    V: Canonicalize,
    S: BuildHasher,
{
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_map("HashMap", self.iter(), true, context, output);
    }
}

impl<T, S> Canonicalize for HashSet<T, S>
where
    T: Canonicalize + Eq + Hash,
    S: BuildHasher,
{
    fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
        write_set("HashSet", self.iter(), true, context, output);
    }
}

macro_rules! tuple {
    ($($name:ident:$index:tt),+ $(,)?) => {
        impl<$($name: Canonicalize),+> Canonicalize for ($($name,)+) {
            fn write_canonical(&self, context: &mut CanonicalContext, output: &mut String) {
                context.write_value("tuple", output, |context, output| {
                    output.push_str("tuple[");
                    $(self.$index.write_canonical(context, output); output.push(';');)+
                    output.push(']');
                });
            }
        }
    };
}

tuple!(A:0);
tuple!(A:0, B:1);
tuple!(A:0, B:1, C:2);
tuple!(A:0, B:1, C:2, D:3);

fn write_sequence<'a, T: Canonicalize + 'a>(
    name: &str,
    values: impl Iterator<Item = &'a T>,
    context: &mut CanonicalContext,
    output: &mut String,
) {
    context.write_value(name, output, |context, output| {
        output.push_str(name);
        output.push('[');
        for value in values {
            value.write_canonical(context, output);
            output.push(';');
        }
        output.push(']');
    });
}

fn write_map<'a, K: Canonicalize + 'a, V: Canonicalize + 'a>(
    name: &str,
    values: impl Iterator<Item = (&'a K, &'a V)>,
    unordered: bool,
    context: &mut CanonicalContext,
    output: &mut String,
) {
    let mut entries = values
        .map(|(key, value)| {
            let mut probe = CanonicalContext::default();
            let mut text = String::new();
            key.write_canonical(&mut probe, &mut text);
            text.push('=');
            value.write_canonical(&mut probe, &mut text);
            text
        })
        .collect::<Vec<_>>();
    if unordered {
        entries.sort();
    }
    context.write_value(name, output, |_, output| {
        output.push_str(name);
        output.push('{');
        for entry in entries {
            output.push_str(&entry);
            output.push(';');
        }
        output.push('}');
    });
}

fn write_set<'a, T: Canonicalize + 'a>(
    name: &str,
    values: impl Iterator<Item = &'a T>,
    unordered: bool,
    context: &mut CanonicalContext,
    output: &mut String,
) {
    let mut entries = values
        .map(|value| {
            let mut probe = CanonicalContext::default();
            let mut text = String::new();
            value.write_canonical(&mut probe, &mut text);
            text
        })
        .collect::<Vec<_>>();
    if unordered {
        entries.sort();
    }
    context.write_value(name, output, |_, output| {
        output.push_str(name);
        output.push('{');
        for entry in entries {
            output.push_str(&entry);
            output.push(';');
        }
        output.push('}');
    });
}

fn write_float(value: f64, tag: &str, output: &mut String) {
    output.push_str(tag);
    output.push(':');
    if value.is_nan() {
        output.push_str("NaN");
    } else if value == f64::INFINITY {
        output.push_str("Infinity");
    } else if value == f64::NEG_INFINITY {
        output.push_str("-Infinity");
    } else if value == 0.0 && value.is_sign_negative() {
        output.push_str("-0");
    } else {
        output.push_str(&value.to_string());
    }
}

fn write_text(output: &mut String, value: &str) {
    output.push('"');
    for character in value.chars() {
        match character {
            '"' => output.push_str("\\\""),
            '\\' => output.push_str("\\\\"),
            '\n' => output.push_str("\\n"),
            '\r' => output.push_str("\\r"),
            '\t' => output.push_str("\\t"),
            value if value.is_control() => {
                output.push_str(&format!("\\u{:04x}", value as u32));
            }
            value => output.push(value),
        }
    }
    output.push('"');
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unordered_collections_are_stable_and_shape_sensitive() {
        let left = HashMap::from([("two", 2_i32), ("one", 1_i32)]);
        let right = HashMap::from([("one", 1_i32), ("two", 2_i32)]);
        let map = capture(&left);
        assert_eq!(map.digest, capture(&right).digest);
        assert_ne!(
            map.digest,
            capture(&vec![("one", 1_i32), ("two", 2_i32)]).digest
        );
        assert!(!map.partial);
        assert!(map.values_digested > 0);
    }

    #[test]
    fn references_preserve_topology_without_runtime_addresses() {
        let shared = Rc::new(String::from("value"));
        let shared_graph = vec![shared.clone(), shared];
        let copied_graph = vec![
            Rc::new(String::from("value")),
            Rc::new(String::from("value")),
        ];
        let first = capture(&shared_graph);
        assert_ne!(first.digest, capture(&copied_graph).digest);
        assert!(!first.rendered.contains("0x"));
    }

    #[test]
    fn skipped_regions_are_visible_and_counted() {
        let mut context = CanonicalContext::default();
        let mut output = String::new();
        context.write_skipped(&mut output, "external:DependencyType");
        assert_eq!(output, "<skipped:\"external:DependencyType\">");
        assert_eq!(context.blocklisted, 1);
        assert!(context.partial);
    }

    #[test]
    fn full_digest_precedes_rendering_cap() {
        let first = capture(&format!("{}A", "x".repeat(RENDERED_CAP + 10)));
        let second = capture(&format!("{}B", "x".repeat(RENDERED_CAP + 10)));
        assert_ne!(first.digest, second.digest);
        assert_eq!(first.rendered, second.rendered);
        assert_eq!(first.rendered_truncated, 1);
        assert!(first.partial);
    }
}
