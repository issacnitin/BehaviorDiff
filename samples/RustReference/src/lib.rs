#![cfg(test)]

#[derive(Clone, Copy)]
struct PrivatePoint {
    left: i32,
    right: i32,
}

enum PrivateState {
    Ready(i32),
    Waiting { value: i32 },
}

fn operation_00(value: i32) -> i32 { value + 1 }
fn operation_01(value: i32) -> i32 { value * 2 }
fn operation_02(value: i32) -> i32 { value - 3 }
fn operation_03(value: i32) -> i32 { value ^ 4 }
fn operation_04(value: i32) -> i32 { value.wrapping_add(5) }
fn operation_05(value: i32) -> i32 { value.wrapping_mul(6) }
fn operation_06(value: i32) -> i32 { value.rotate_left(1) }
fn operation_07(value: i32) -> i32 { value.rotate_right(2) }
fn operation_08(value: i32) -> i32 { value | 8 }
fn operation_09(value: i32) -> i32 { value & 0x7fff }
fn operation_10(value: i32) -> i32 { value + 11 }
fn operation_11(value: i32) -> i32 { value * 3 }
fn operation_12(value: i32) -> i32 { value - 13 }
fn operation_13(value: i32) -> i32 { value ^ 14 }
fn operation_14(value: i32) -> i32 { value.wrapping_add(15) }
fn operation_15(value: i32) -> i32 { value.wrapping_mul(2) }
fn operation_16(value: i32) -> i32 { value.rotate_left(3) }
fn operation_17(value: i32) -> i32 { value.rotate_right(1) }
fn operation_18(value: i32) -> i32 { value | 18 }
fn operation_19(value: i32) -> i32 { value & 0x3fff }
fn operation_20(value: i32) -> i32 { value + 21 }
fn operation_21(value: i32) -> i32 { value * 4 }
fn operation_22(value: i32) -> i32 { value - 23 }
fn operation_23(value: i32) -> i32 { value ^ 24 }
fn operation_24(value: i32) -> i32 { value.wrapping_add(25) }
fn operation_25(value: i32) -> i32 { value.wrapping_mul(3) }
fn operation_26(value: i32) -> i32 { value.rotate_left(2) }
fn operation_27(value: i32) -> i32 { value.rotate_right(3) }
fn operation_28(value: i32) -> i32 { value | 28 }
fn operation_29(value: i32) -> i32 { value & 0x1fff }
fn operation_30(value: i32) -> i32 { value + 31 }
fn operation_31(value: i32) -> i32 { value * 5 }
fn operation_32(value: i32) -> i32 { value - 33 }
fn operation_33(value: i32) -> i32 { value ^ 34 }
fn operation_34(value: i32) -> i32 { value.wrapping_add(35) }
fn operation_35(value: i32) -> i32 { value.wrapping_mul(4) }
fn operation_36(value: i32) -> i32 { value.rotate_left(4) }
fn operation_37(value: i32) -> i32 { value.rotate_right(4) }
fn operation_38(value: i32) -> i32 { value | 38 }
fn operation_39(value: i32) -> i32 { value & 0x0fff }
fn operation_40(value: i32) -> i32 { value + 41 }
fn operation_41(value: i32) -> i32 { value * 6 }
fn operation_42(value: i32) -> i32 { value - 43 }
fn operation_43(value: i32) -> i32 { value ^ 44 }
fn operation_44(value: i32) -> i32 { value.wrapping_add(45) }
fn operation_45(value: i32) -> i32 { value.wrapping_mul(5) }
fn operation_46(value: i32) -> i32 { value.rotate_left(5) }
fn operation_47(value: i32) -> i32 { value.rotate_right(5) }
fn operation_48(value: i32) -> i32 { value | 48 }
fn operation_49(value: i32) -> i32 { value & 0x07ff }
fn operation_50(value: i32) -> i32 { value + 51 }
fn operation_51(value: i32) -> i32 { value * 7 }
fn operation_52(value: i32) -> i32 { value - 53 }
fn operation_53(value: i32) -> i32 { value ^ 54 }
fn operation_54(value: i32) -> i32 { value.wrapping_add(55) }
fn operation_55(value: i32) -> i32 { value.wrapping_mul(6) }
fn operation_56(value: i32) -> i32 { value.rotate_left(6) }
fn operation_57(value: i32) -> i32 { value.rotate_right(6) }
fn operation_58(value: i32) -> i32 { value | 58 }
fn operation_59(value: i32) -> i32 { value & 0x03ff }

fn run_suite(seed: i32) -> i32 {
    let point = PrivatePoint { left: seed, right: seed + 1 };
    let state = if seed % 2 == 0 {
        PrivateState::Ready(point.left + point.right)
    } else {
        PrivateState::Waiting { value: point.left + point.right }
    };
    let mut value = match state {
        PrivateState::Ready(value) => value,
        PrivateState::Waiting { value } => value,
    };
    value = operation_00(value);
    value = operation_01(value);
    value = operation_02(value);
    value = operation_03(value);
    value = operation_04(value);
    value = operation_05(value);
    value = operation_06(value);
    value = operation_07(value);
    value = operation_08(value);
    value = operation_09(value);
    value = operation_10(value);
    value = operation_11(value);
    value = operation_12(value);
    value = operation_13(value);
    value = operation_14(value);
    value = operation_15(value);
    value = operation_16(value);
    value = operation_17(value);
    value = operation_18(value);
    value = operation_19(value);
    value = operation_20(value);
    value = operation_21(value);
    value = operation_22(value);
    value = operation_23(value);
    value = operation_24(value);
    value = operation_25(value);
    value = operation_26(value);
    value = operation_27(value);
    value = operation_28(value);
    value = operation_29(value);
    value = operation_30(value);
    value = operation_31(value);
    value = operation_32(value);
    value = operation_33(value);
    value = operation_34(value);
    value = operation_35(value);
    value = operation_36(value);
    value = operation_37(value);
    value = operation_38(value);
    value = operation_39(value);
    value = operation_40(value);
    value = operation_41(value);
    value = operation_42(value);
    value = operation_43(value);
    value = operation_44(value);
    value = operation_45(value);
    value = operation_46(value);
    value = operation_47(value);
    value = operation_48(value);
    value = operation_49(value);
    value = operation_50(value);
    value = operation_51(value);
    value = operation_52(value);
    value = operation_53(value);
    value = operation_54(value);
    value = operation_55(value);
    value = operation_56(value);
    value = operation_57(value);
    value = operation_58(value);
    operation_59(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reference_case_0() { assert_ne!(run_suite(0), i32::MIN); }

    #[test]
    fn reference_case_1() { assert_ne!(run_suite(1), i32::MIN); }

    #[test]
    fn reference_case_2() { assert_ne!(run_suite(2), i32::MIN); }

    #[test]
    fn reference_case_3() { assert_ne!(run_suite(3), i32::MIN); }

    #[test]
    fn reference_case_4() { assert_ne!(run_suite(4), i32::MIN); }

    #[test]
    fn reference_case_5() { assert_ne!(run_suite(5), i32::MIN); }
}
